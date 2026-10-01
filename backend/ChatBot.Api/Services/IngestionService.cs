using System.Collections.Concurrent;
using ChatBot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

public enum IngestionStage
{
    Queued,
    Storing,      // 原始檔存入物件儲存
    Parsing,      // 解析與切分
    Embedding,    // 產生向量並寫入 Qdrant
    Warmup,       // 預算 KV
    Done,
    Failed
}

public sealed class IngestionJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "";
    public string Title { get; set; } = "";
    public IngestionStage Stage { get; set; } = IngestionStage.Queued;
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    /// <summary>原始檔在物件儲存中的識別碼。</summary>
    public string? StorageKey { get; set; }
    public string StorageBackend { get; set; } = "";

    public int ArticleCount { get; set; }
    public int ChunkCount { get; set; }
    public int EmbeddedCount { get; set; }
    public int ReplacedCount { get; set; }

    public bool WarmupRequested { get; set; }
    public WarmupProgress? Warmup { get; set; }

    public string? Error { get; set; }
    public List<string> Warnings { get; } = new();

    public long ElapsedMilliseconds =>
        (long)((FinishedAt ?? DateTime.UtcNow) - StartedAt).TotalMilliseconds;
}

/// <summary>
/// 進行中與最近完成的匯入工作。
///
/// 為什麼需要它：一份民法要跑 300 多次 embedding 再加上 KV 預熱，
/// 整段耗時以分鐘計，撐不過一個 HTTP 請求。因此上傳端點只負責接檔與排程，
/// 前端以 job id 輪詢進度。
///
/// 存在記憶體即可——工作狀態的價值只在它進行中的那幾分鐘，
/// 服務重啟後未完成的工作本來就得重來，持久化沒有意義。
/// </summary>
public interface IIngestionJobStore
{
    IngestionJob Create(string fileName, string title, bool warmup);
    IngestionJob? Get(string id);
    IReadOnlyList<IngestionJob> Recent(int limit = 20);
}

public class IngestionJobStore : IIngestionJobStore
{
    private readonly ConcurrentDictionary<string, IngestionJob> _jobs = new();
    private const int MaxRetained = 100;

    public IngestionJob Create(string fileName, string title, bool warmup)
    {
        var job = new IngestionJob
        {
            FileName = fileName,
            Title = title,
            WarmupRequested = warmup
        };

        _jobs[job.Id] = job;

        // 保留上限，避免長時間運行後無限累積
        if (_jobs.Count > MaxRetained)
        {
            var oldest = _jobs.Values
                .OrderBy(j => j.StartedAt)
                .Take(_jobs.Count - MaxRetained)
                .Select(j => j.Id);

            foreach (var id in oldest)
            {
                _jobs.TryRemove(id, out _);
            }
        }

        return job;
    }

    public IngestionJob? Get(string id) => _jobs.GetValueOrDefault(id);

    public IReadOnlyList<IngestionJob> Recent(int limit = 20) =>
        _jobs.Values.OrderByDescending(j => j.StartedAt).Take(limit).ToList();
}

public interface IIngestionService
{
    /// <summary>
    /// 跑完整條資料準備流程：保存原始檔 → 解析切分 → 向量化寫入 Qdrant →（可選）預算 KV。
    /// </summary>
    Task RunAsync(IngestionJob job, string pdfPath, CancellationToken ct = default);
}

/// <summary>
/// 上傳後的資料準備流程。
///
/// 流程刻意做成「先保存原始檔，再解析」：切分規則會改（2026/09/24 就整組改過），
/// 留著原始檔才能重新切分，否則每次調整顆粒度都得回頭找當初的 PDF。
///
/// 重新上傳同一部法典時採**取代**語意：先刪掉該 title 既有的全部片段再寫入新的。
/// 不這樣做的話，切分規則一改，舊片段會與新片段並存於同一個集合中，
/// 檢索會同時撈到兩種顆粒度的內容——那是最難察覺的一種髒資料。
/// </summary>
public class IngestionService : IIngestionService
{
    private readonly IObjectStorage _storage;
    private readonly ILegalDocumentParser _parser;
    private readonly IEmbeddingService _embedding;
    private readonly IQdrantService _qdrant;
    private readonly IKvWarmupService _warmup;
    private readonly RAGSettings _settings;
    private readonly ILogger<IngestionService> _logger;

    private const string Collection = "legal_documents";

    public IngestionService(
        IObjectStorage storage,
        ILegalDocumentParser parser,
        IEmbeddingService embedding,
        IQdrantService qdrant,
        IKvWarmupService warmup,
        IOptions<RAGSettings> settings,
        ILogger<IngestionService> logger)
    {
        _storage = storage;
        _parser = parser;
        _embedding = embedding;
        _qdrant = qdrant;
        _warmup = warmup;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task RunAsync(IngestionJob job, string pdfPath, CancellationToken ct = default)
    {
        try
        {
            // ── 1. 保存原始檔 ──────────────────────────────────────
            job.Stage = IngestionStage.Storing;
            job.StorageBackend = _storage.BackendName;

            await using (var fs = File.OpenRead(pdfPath))
            {
                var stored = await _storage.SaveAsync(fs, job.FileName, "application/pdf", ct);
                job.StorageKey = stored.Key;
            }

            // ── 2. 解析與切分 ──────────────────────────────────────
            job.Stage = IngestionStage.Parsing;

            var parsed = await _parser.ParsePdfAsync(
                pdfPath, string.IsNullOrWhiteSpace(job.Title) ? null : job.Title, ct);

            job.Title = parsed.Title;
            job.ArticleCount = parsed.ArticleCount;
            job.ChunkCount = parsed.Chunks.Count;

            if (!string.IsNullOrEmpty(parsed.Warning))
            {
                job.Warnings.Add(parsed.Warning);
            }

            if (parsed.Chunks.Count == 0)
            {
                throw new InvalidOperationException(
                    parsed.Warning ?? "解析後沒有任何片段，請確認檔案是否為條列式法規");
            }

            WarnOnDuplicateLabels(parsed, job);

            // ── 3. 向量化並寫入 Qdrant ────────────────────────────
            job.Stage = IngestionStage.Embedding;
            await _qdrant.InitializeCollectionAsync(Collection);

            // 取代語意：同名法典的舊片段先清掉
            job.ReplacedCount = await _qdrant.DeleteByTitleAsync(Collection, parsed.Title, ct);

            if (job.ReplacedCount > 0)
            {
                _logger.LogInformation(
                    "「{Title}」已存在 {Count} 個舊片段，已刪除後重新寫入",
                    parsed.Title, job.ReplacedCount);
            }

            foreach (var chunk in parsed.Chunks)
            {
                ct.ThrowIfCancellationRequested();

                var vector = await _embedding.GenerateEmbeddingAsync(chunk.Content, ct);

                var payload = new KeyValuePair<string, string>[]
                {
                    new("title", chunk.Title),
                    // content 已含條號那一行；後端直接拿它當片段文字與快取鍵，
                    // 不可再於前面補上任何隨位置變動的標記
                    new("content", chunk.Content),
                    new("chapter", chunk.Chapter),
                    new("articleNumber", chunk.Article),
                    new("nChars", chunk.NChars.ToString()),
                    new("sourceFile", chunk.SourceFile),
                    new("storageKey", job.StorageKey ?? "")
                };

                await _qdrant.UpsertDocumentAsync(Collection, chunk.Id, vector, payload);
                job.EmbeddedCount++;
            }

            // ── 4. 預算 KV（可選）──────────────────────────────────
            if (job.WarmupRequested)
            {
                job.Stage = IngestionStage.Warmup;
                job.Warmup = await _warmup.WarmupAsync(
                    parsed.Chunks.Select(c => c.Content).ToList(), null, ct);
            }

            job.Stage = IngestionStage.Done;
            job.FinishedAt = DateTime.UtcNow;

            _logger.LogInformation(
                "匯入完成：{Title} — {Articles} 條 → {Chunks} 片段，耗時 {Sec:F1} 秒",
                job.Title, job.ArticleCount, job.ChunkCount, job.ElapsedMilliseconds / 1000.0);
        }
        catch (Exception ex)
        {
            job.Stage = IngestionStage.Failed;
            job.Error = ex.Message;
            job.FinishedAt = DateTime.UtcNow;
            _logger.LogError(ex, "匯入失敗：{File}", job.FileName);

            await CleanUpOrphanAsync(job);
        }
        finally
        {
            // 暫存檔已經複製進物件儲存，這裡的副本沒有保留價值
            TryDelete(pdfPath);
        }
    }

    /// <summary>
    /// 匯入失敗時清掉已保存但無人引用的原始檔。
    ///
    /// 為什麼會有孤兒：流程是「先保存原始檔、再解析」，因此解析失敗時
    /// 檔案已經寫進去了。2026/09/30 一次解析錯誤就留下兩份無用的公司法 PDF，
    /// 而且從外觀看不出哪一份是有效的——在共用機器上這種累積比遺失更麻煩。
    ///
    /// ⚠ 只在**完全沒有任何片段寫進 Qdrant** 時才刪。
    /// 一旦有片段寫入，它們的 payload 就記著這個 storageKey，刪掉檔案會讓
    /// 那些片段指向不存在的來源——那比留一份孤兒檔案糟得多。
    /// 因此預熱階段失敗（此時片段早已寫入）不會觸發清理。
    ///
    /// 清理本身失敗不會覆蓋原本的錯誤：使用者要知道的是匯入為什麼失敗，
    /// 而不是善後動作的細節。
    /// </summary>
    private async Task CleanUpOrphanAsync(IngestionJob job)
    {
        if (string.IsNullOrEmpty(job.StorageKey)) return;

        if (job.EmbeddedCount > 0)
        {
            job.Warnings.Add(
                $"已有 {job.EmbeddedCount} 個片段寫入知識庫，原始檔保留（{job.StorageKey}）");
            return;
        }

        try
        {
            var removed = await _storage.DeleteAsync(job.StorageKey, CancellationToken.None);

            if (removed)
            {
                _logger.LogInformation(
                    "匯入失敗，已清除無人引用的原始檔 {Key}", job.StorageKey);
                job.Warnings.Add("匯入失敗，已自動清除保存的原始檔");
                job.StorageKey = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清除原始檔失敗，將留下孤兒檔案：{Key}", job.StorageKey);
            job.Warnings.Add($"自動清除原始檔失敗，需手動處理：{job.StorageKey}");
        }
    }

    /// <summary>
    /// 檢查同一份文件裡有沒有重複的條號標籤。
    ///
    /// 這是切分是否正常最靈敏的指標：2026/09/24 那次失效，症狀正是
    /// 51 組重複標籤、涉及 126 個片段——兩個內容不同的片段掛著同一個條號。
    /// 當時是靠人工翻檢索結果才發現的，所以現在讓它自己叫出來。
    /// </summary>
    private void WarnOnDuplicateLabels(ParseResult parsed, IngestionJob job)
    {
        var dupes = parsed.Chunks
            .GroupBy(c => c.Article)
            .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1)
            .ToList();

        if (dupes.Count == 0) return;

        var affected = dupes.Sum(g => g.Count());
        var msg = $"偵測到 {dupes.Count} 組重複的條號標籤（涉及 {affected} 個片段），" +
                  $"例如「{dupes[0].Key}」。這通常代表切分沒有正常運作，" +
                  "請檢查該 PDF 的條號是否獨立成行";

        job.Warnings.Add(msg);
        _logger.LogWarning("{Message}", msg);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刪除暫存檔失敗：{Path}", path);
        }
    }
}
