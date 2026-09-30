using ChatBot.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Api.Controllers;

public sealed class WarmupRequest
{
    /// <summary>只預熱這部法典的片段；留空則全部。</summary>
    public string? Law { get; set; }

    /// <summary>上限，供試跑時只暖一小部分。0 或省略表示不限。</summary>
    public int? Limit { get; set; }
}

public sealed class AddArticleTextRequest
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";

    /// <summary>是否在寫入後立即預算 KV。</summary>
    public bool Warmup { get; set; }
}

/// <summary>
/// 知識庫管理：上傳、查詢、預熱。
///
/// 完整的資料準備流程是
///   原始檔保存 → 解析切分 → 向量化寫入 Qdrant →（可選）預算 KV 進 LMCache
/// 由 IngestionService 執行，這裡只負責收檔、排程與回報進度。
///
/// 為什麼做成非同步作業：一部民法有 1,439 條、切成三百多個片段，
/// 每段都要打一次外部 embedding，再加上 KV 預熱，整段耗時以分鐘計，
/// 撐不過一個 HTTP 請求。因此上傳端點立刻回傳 jobId，前端輪詢進度。
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class KnowledgeBaseController : ControllerBase
{
    private readonly IQdrantService _qdrant;
    private readonly IEmbeddingService _embedding;
    private readonly ILegalDocumentParser _parser;
    private readonly IKvWarmupService _warmup;
    private readonly IIngestionJobStore _jobs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KnowledgeBaseController> _logger;

    private const string Collection = "legal_documents";

    /// <summary>上傳大小上限。法規 PDF 通常在數 MB 之譜，100 MB 已相當寬裕。</summary>
    private const long MaxUploadBytes = 100L * 1024 * 1024;

    public KnowledgeBaseController(
        IQdrantService qdrant,
        IEmbeddingService embedding,
        ILegalDocumentParser parser,
        IKvWarmupService warmup,
        IIngestionJobStore jobs,
        IServiceScopeFactory scopeFactory,
        ILogger<KnowledgeBaseController> logger)
    {
        _qdrant = qdrant;
        _embedding = embedding;
        _parser = parser;
        _warmup = warmup;
        _jobs = jobs;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════
    //  上傳
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 上傳法規 PDF，啟動資料準備流程。立即回傳 jobId，進度另以 jobs/{id} 查詢。
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromQuery] string? title = null,
        [FromQuery] bool warmup = true,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "請選擇檔案" });
        }

        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                error = "目前只支援 PDF",
                hint = "若要貼純文字請改用 POST /api/knowledgebase"
            });
        }

        // 先把上傳內容落到暫存檔：解析腳本是另一個行程，需要一個它讀得到的路徑。
        // IngestionService 會在流程結束後刪除它。
        var temp = Path.Combine(Path.GetTempPath(), $"upload_{Guid.NewGuid():N}.pdf");

        await using (var fs = System.IO.File.Create(temp))
        {
            await file.CopyToAsync(fs, ct);
        }

        var job = _jobs.Create(file.FileName, title ?? "", warmup);

        // 背景執行。這裡刻意不沿用請求的 CancellationToken——
        // 回應一送出它就會被取消，整個匯入會在第一個 await 處中止。
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<IIngestionService>();
            await ingestion.RunAsync(job, temp, CancellationToken.None);
        }, CancellationToken.None);

        _logger.LogInformation("已排入匯入工作 {JobId}：{File}", job.Id, file.FileName);

        return Accepted(new
        {
            jobId = job.Id,
            fileName = job.FileName,
            warmup = job.WarmupRequested,
            statusUrl = $"/api/knowledgebase/jobs/{job.Id}"
        });
    }

    /// <summary>查詢單一匯入工作的進度。</summary>
    [HttpGet("jobs/{id}")]
    public IActionResult GetJob(string id)
    {
        var job = _jobs.Get(id);
        return job is null ? NotFound(new { error = $"找不到工作 {id}" }) : Ok(Describe(job));
    }

    /// <summary>列出最近的匯入工作。</summary>
    [HttpGet("jobs")]
    public IActionResult ListJobs([FromQuery] int limit = 20)
        => Ok(new { jobs = _jobs.Recent(limit).Select(Describe) });

    private static object Describe(IngestionJob j) => new
    {
        jobId = j.Id,
        stage = j.Stage.ToString().ToLowerInvariant(),
        fileName = j.FileName,
        title = j.Title,
        articleCount = j.ArticleCount,
        chunkCount = j.ChunkCount,
        embeddedCount = j.EmbeddedCount,
        replacedCount = j.ReplacedCount,
        storageKey = j.StorageKey,
        storageBackend = j.StorageBackend,
        elapsedMilliseconds = j.ElapsedMilliseconds,
        warmupRequested = j.WarmupRequested,
        warmup = j.Warmup is null ? null : new
        {
            totalChunks = j.Warmup.TotalChunks,
            processedChunks = j.Warmup.ProcessedChunks,
            batches = j.Warmup.Batches,
            failedBatches = j.Warmup.FailedBatches,
            totalPromptTokens = j.Warmup.TotalPromptTokens,
            totalCachedTokens = j.Warmup.TotalCachedTokens,
            elapsedMilliseconds = j.Warmup.ElapsedMilliseconds,
            errors = j.Warmup.Errors
        },
        warnings = j.Warnings,
        error = j.Error
    };

    // ══════════════════════════════════════════════════════════════
    //  純文字新增
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 以純文字新增法規。走的是與 PDF 上傳**完全相同**的解析器。
    ///
    /// 這點很重要：兩條路徑若用不同的切法，同一個集合裡會並存兩種顆粒度的片段，
    /// 檢索會同時撈到兩種——那是最難察覺的一種髒資料。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> AddArticle(
        [FromBody] AddArticleTextRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { error = "請提供 title（法典名稱）" });

        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "請提供 content" });

        var parsed = await _parser.ParseTextAsync(request.Content, request.Title, "(text)", ct);

        if (parsed.Chunks.Count == 0)
        {
            return BadRequest(new
            {
                error = parsed.Warning ?? "解析後沒有任何片段",
                hint = "條號必須獨立成行，例如「第 1 條」自成一行、內文另起一行"
            });
        }

        await _qdrant.InitializeCollectionAsync(Collection);
        var replaced = await _qdrant.DeleteByTitleAsync(Collection, parsed.Title, ct);

        foreach (var chunk in parsed.Chunks)
        {
            var vector = await _embedding.GenerateEmbeddingAsync(chunk.Content, ct);

            await _qdrant.UpsertDocumentAsync(Collection, chunk.Id, vector, new[]
            {
                new KeyValuePair<string, string>("title", chunk.Title),
                new KeyValuePair<string, string>("content", chunk.Content),
                new KeyValuePair<string, string>("chapter", chunk.Chapter),
                new KeyValuePair<string, string>("articleNumber", chunk.Article),
                new KeyValuePair<string, string>("nChars", chunk.NChars.ToString()),
                new KeyValuePair<string, string>("sourceFile", "(text)")
            });
        }

        WarmupProgress? warmupResult = null;
        if (request.Warmup)
        {
            warmupResult = await _warmup.WarmupAsync(
                parsed.Chunks.Select(c => c.Content).ToList(), null, ct);
        }

        return Ok(new
        {
            title = parsed.Title,
            articleCount = parsed.ArticleCount,
            chunkCount = parsed.Chunks.Count,
            replacedCount = replaced,
            warmup = warmupResult,
            chunks = parsed.Chunks.Select(c => new { c.Article, c.Chapter, c.NChars })
        });
    }

    // ══════════════════════════════════════════════════════════════
    //  KV 預熱
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 對既有片段預算 KV。
    ///
    /// 這個端點獨立存在的理由：in-process 模式下 LMCache 的 L2 索引存在記憶體，
    /// 服務重啟後磁碟上的 KV 檔案雖然還在、內容也完整，但索引不會重建，
    /// 讀不到，等同冷啟（驗證報告 8.4、12.4）。
    /// 因此預熱不是一次性的資料準備，而是**每次啟動後都要重跑**的動作。
    /// </summary>
    [HttpPost("warmup")]
    public async Task<IActionResult> Warmup(
        [FromBody] WarmupRequest? request, CancellationToken ct = default)
    {
        var all = await _qdrant.ScrollAllAsync(Collection);

        var selected = all
            .Where(c => string.IsNullOrWhiteSpace(request?.Law)
                        || (c.Title ?? "").Contains(request!.Law!))
            .Select(c => c.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        if (request?.Limit is > 0)
        {
            selected = selected.Take(request.Limit.Value).ToList();
        }

        if (selected.Count == 0)
        {
            return BadRequest(new
            {
                error = "沒有符合條件的片段",
                availableLaws = all.Select(c => c.Title).Distinct().OrderBy(x => x)
            });
        }

        var result = await _warmup.WarmupAsync(selected, null, ct);

        return Ok(new
        {
            law = request?.Law,
            totalChunks = result.TotalChunks,
            processedChunks = result.ProcessedChunks,
            batches = result.Batches,
            failedBatches = result.FailedBatches,
            totalPromptTokens = result.TotalPromptTokens,
            totalCachedTokens = result.TotalCachedTokens,
            elapsedMilliseconds = result.ElapsedMilliseconds,
            errors = result.Errors,
            note = "服務重啟後 L2 索引不會重建，需要重新執行本端點"
        });
    }

    // ══════════════════════════════════════════════════════════════
    //  查詢與維護
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 列出知識庫現況：每部法典的片段數與字數分佈。
    ///
    /// 先前這個端點回傳的是寫死的示範資料，看起來正常卻與實際內容無關。
    /// 現在直接從 Qdrant 統計。
    /// </summary>
    [HttpGet("documents")]
    public async Task<IActionResult> GetDocuments([FromQuery] string? law = null)
    {
        var all = await _qdrant.ScrollAllAsync(Collection);

        var laws = all
            .GroupBy(c => c.Title ?? "")
            .Select(g => new
            {
                law = g.Key,
                chunks = g.Count(),
                totalChars = g.Sum(c => c.Content.Length),
                medianChars = Median(g.Select(c => c.Content.Length).ToList()),
                chapters = g.Select(c => c.Chapter).Distinct().Count()
            })
            .OrderBy(x => x.law)
            .ToList();

        var chunks = all
            .Where(c => string.IsNullOrWhiteSpace(law) || (c.Title ?? "").Contains(law))
            .OrderBy(c => c.Title)
            .Select(c => new
            {
                id = c.Id,
                title = c.Title,
                articleNumber = c.ArticleNumber,
                chapter = c.Chapter,
                chars = c.Content.Length
            })
            .ToList();

        return Ok(new { totalChunks = all.Count, laws, chunks });
    }

    private static int Median(List<int> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string query, [FromQuery] int topK = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { error = "請提供 query" });

        await _qdrant.InitializeCollectionAsync(Collection);
        var vector = await _embedding.GenerateEmbeddingAsync(query);
        var results = await _qdrant.SearchSimilarAsync(Collection, vector, topK);

        return Ok(new
        {
            query,
            count = results.Count,
            results = results.Select(r => new
            {
                r.Result.Id,
                r.Result.Title,
                r.Result.Content,
                r.Result.Chapter,
                r.Result.ArticleNumber,
                score = r.Score
            })
        });
    }

    /// <summary>刪除某一部法典的全部片段。</summary>
    [HttpDelete("laws/{law}")]
    public async Task<IActionResult> DeleteLaw(string law, CancellationToken ct = default)
    {
        var deleted = await _qdrant.DeleteByTitleAsync(Collection, law, ct);

        return deleted == 0
            ? NotFound(new { error = $"找不到法典「{law}」的片段" })
            : Ok(new { law, deleted });
    }

    [HttpDelete("documents/{id}")]
    public async Task<IActionResult> DeleteDocument(string id)
    {
        await _qdrant.DeleteDocumentAsync(Collection, id);
        return Ok(new { id, deleted = true });
    }

    /// <summary>清空整個集合。破壞性操作，需帶 confirm=yes。</summary>
    [HttpDelete("clear")]
    public async Task<IActionResult> Clear([FromQuery] string? confirm = null)
    {
        if (confirm != "yes")
        {
            return BadRequest(new
            {
                error = "這會刪除知識庫的全部內容，請加上 ?confirm=yes 確認"
            });
        }

        await _qdrant.TruncateCollectionAsync(Collection);
        _logger.LogWarning("知識庫已被清空");
        return Ok(new { cleared = true });
    }
}
