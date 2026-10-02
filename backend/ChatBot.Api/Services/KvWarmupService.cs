using ChatBot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

public sealed class WarmupProgress
{
    public int TotalChunks { get; set; }
    public int ProcessedChunks { get; set; }
    public int Batches { get; set; }
    public int FailedBatches { get; set; }
    public long ElapsedMilliseconds { get; set; }

    /// <summary>預熱過程中引擎回報的 prompt token 總量，可用來估算實際算了多少。</summary>
    public long TotalPromptTokens { get; set; }

    /// <summary>其中命中既有快取的 token 數。第一次預熱應該接近 0。</summary>
    public long TotalCachedTokens { get; set; }

    public List<string> Errors { get; set; } = new();
}

public interface IKvWarmupService
{
    /// <summary>
    /// 把指定片段送過推論引擎一遍，使其 KV 進入 LMCache。
    /// </summary>
    Task<WarmupProgress> WarmupAsync(
        IReadOnlyList<string> chunkContents,
        IProgress<WarmupProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// KV 預熱：讓條文片段的 KV 預先進入 LMCache，使第一位使用者不必承受冷啟成本。
///
/// ── 原理 ──────────────────────────────────────────────────────
/// 片段的 KV 只有在它**實際出現在某個送給模型的 prompt 裡**時才會產生。
/// 因此預熱就是拿這些片段組成 prompt、送出去、丟棄生成結果。
/// LMCache 以片段內容的雜湊為鍵，與片段當時的位置無關，所以之後真實問答
/// 不論檢索到什麼順序都能命中。
///
/// max_tokens 設為 1：預熱要的是引擎把 prompt 算過一遍，生成的字沒有用途。
///
/// ── 兩個必須知道的限制 ────────────────────────────────────────
/// 1. **重啟後就沒了。** in-process 模式下 L2 的索引存在記憶體，重啟不會從
///    磁碟重建（驗證報告 8.4、12.4）。磁碟上的 KV 檔案還在、內容也完整，
///    但讀不到。因此預熱必須在每次服務啟動後重跑，不是「算一次存著」。
///
/// 2. **預熱同時把正確性風險提前。** 這裡算出來的 KV 是「片段在預熱那個位置」
///    的 KV；之後真實問答把它擺到別的位置使用，就是驗證報告 14.6 測到會
///    改變輸出的那種跨位置複用。換言之，預熱讓命中率變好看，但也讓每一次
///    問答都落在已知會影響條號引用正確性的路徑上。
///    在正確性題組做完之前，這個服務適合用於效能展示，不適合直接上生產。
/// </summary>
public class KvWarmupService : IKvWarmupService
{
    private readonly IPromptBuilder _promptBuilder;
    private readonly ILLMService _llm;
    private readonly IChunkCacheTracker _tracker;
    private readonly RAGSettings _settings;
    private readonly ILogger<KvWarmupService> _logger;

    /// <summary>
    /// 預熱用的問句。內容不重要——它只佔 prompt 的最後一段，
    /// 片段的 KV 與它無關。但必須固定，否則每批的問句不同會讓
    /// prompt 尾端長度浮動，徒增判讀困難。
    /// </summary>
    private const string WarmupQuestion = "請摘要以上條文。";

    public KvWarmupService(
        IPromptBuilder promptBuilder,
        ILLMService llm,
        IChunkCacheTracker tracker,
        IOptions<RAGSettings> settings,
        ILogger<KvWarmupService> logger)
    {
        _promptBuilder = promptBuilder;
        _llm = llm;
        _tracker = tracker;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<WarmupProgress> WarmupAsync(
        IReadOnlyList<string> chunkContents,
        IProgress<WarmupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var state = new WarmupProgress { TotalChunks = chunkContents.Count };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 每批的片段數刻意對齊檢索時的 topK：批次大小會決定 prompt 的總長度，
        // 而長度會影響引擎的分批行為。用相同的長度預熱，命中狀況才與真實問答可比。
        var batchSize = Math.Max(1, _settings.WarmupBatchSize);

        _logger.LogInformation(
            "開始 KV 預熱：{Total} 個片段，每批 {Batch} 段", chunkContents.Count, batchSize);

        for (var i = 0; i < chunkContents.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var slice = chunkContents.Skip(i).Take(batchSize).ToList();
            var chunks = slice.Select(c => new RetrievedChunk { Content = c }).ToList();
            state.Batches++;

            try
            {
                // 預熱不帶對話歷史：要寫進快取的是條文片段的 KV，
                // 帶歷史只會讓最後那一段每次都不同，沒有任何好處。
                var prompt = await _promptBuilder.BuildAsync(
                    chunks, WarmupQuestion, cancellationToken: ct);
                var result = await _llm.GenerateFromTokensAsync(prompt.Tokens, ct, maxTokens: 1);

                state.TotalPromptTokens += result.PromptTokens;
                state.TotalCachedTokens += result.CachedTokens;
                state.ProcessedChunks += slice.Count;

                // 送出成功才登記，否則重排會以為片段已在快取中
                _tracker.MarkSent(slice);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 單批失敗不中斷整體：預熱是盡力而為的最佳化，
                // 少暖到幾段只是那幾段第一次被問到時慢一點，不影響正確性。
                state.FailedBatches++;
                var msg = $"第 {state.Batches} 批預熱失敗（片段 {i + 1}–{i + slice.Count}）：{ex.Message}";
                state.Errors.Add(msg);
                _logger.LogWarning(ex, "{Message}", msg);
            }

            state.ElapsedMilliseconds = sw.ElapsedMilliseconds;
            progress?.Report(state);
        }

        sw.Stop();
        state.ElapsedMilliseconds = sw.ElapsedMilliseconds;

        _logger.LogInformation(
            "KV 預熱完成：{Done}/{Total} 片段、{Batches} 批（失敗 {Failed}），" +
            "prompt 共 {Prompt:N0} tokens、其中命中 {Cached:N0}，耗時 {Sec:F1} 秒",
            state.ProcessedChunks, state.TotalChunks, state.Batches, state.FailedBatches,
            state.TotalPromptTokens, state.TotalCachedTokens, sw.ElapsedMilliseconds / 1000.0);

        return state;
    }
}
