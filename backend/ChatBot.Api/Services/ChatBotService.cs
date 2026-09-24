using ChatBot.Api.Configuration;
using ChatBot.Api.Models;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

public interface IChatBotService
{
    Task<ChatResponse> GetReplyAsync(ChatRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 法律領域聊天機器人（民法與公司法）。
///
/// 流程：
///   1. 以問題向量在 Qdrant 檢索條文片段
///   2. PromptBuilder 依 CacheBlend 的格式組成 token id 陣列
///   3. LLMService 送往自架 vLLM，串流回傳並量測 TTFT 與快取命中率
///
/// 這裡刻意不做「失敗就退回內建知識庫」：
///   原本的備用字典會把 RAG 與 LLM 的所有失敗靜默吞掉，使用者看到的是
///   一段寫死的文字，而那些文字本身有過時與錯誤的條文（例如民法第 12 條
///   的成年年齡已於 2023/01/01 修正為十八歲）。法律問答寧可回報查無資料，
///   也不該輸出無法追溯來源的條文。
/// </summary>
public class ChatBotService : IChatBotService
{
    private readonly IQdrantService _qdrantService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IPromptBuilder _promptBuilder;
    private readonly ILLMService _llmService;
    private readonly IChunkCacheTracker _cacheTracker;
    private readonly RAGSettings _settings;
    private readonly ILogger<ChatBotService> _logger;

    private const string Disclaimer =
        "⚠️ 免責聲明：以上資訊僅供參考，不構成正式法律意見。如有具體法律問題，建議諮詢專業律師。";

    public ChatBotService(
        IQdrantService qdrantService,
        IEmbeddingService embeddingService,
        IPromptBuilder promptBuilder,
        ILLMService llmService,
        IChunkCacheTracker cacheTracker,
        IOptions<RAGSettings> settings,
        ILogger<ChatBotService> logger)
    {
        _qdrantService = qdrantService;
        _embeddingService = embeddingService;
        _promptBuilder = promptBuilder;
        _llmService = llmService;
        _cacheTracker = cacheTracker;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<ChatResponse> GetReplyAsync(
        ChatRequest request, CancellationToken cancellationToken = default)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        await _qdrantService.InitializeCollectionAsync();

        var queryVector = await _embeddingService.GenerateEmbeddingAsync(
            request.Message, cancellationToken);

        var searchResults = await _qdrantService.SearchSimilarAsync(
            "legal_documents", queryVector, topK: _settings.RetrievalTopK);

        if (searchResults.Count == 0)
        {
            totalStopwatch.Stop();
            _logger.LogWarning("知識庫沒有檢索到任何條文，問題：{Message}", request.Message);

            return new ChatResponse
            {
                Reply = $"查無相關條文，無法回答這個問題。\n\n{Disclaimer}",
                TotalMilliseconds = totalStopwatch.ElapsedMilliseconds
            };
        }

        var chunks = searchResults
            .Select(r => new RetrievedChunk
            {
                ArticleNumber = r.Result.ArticleNumber ?? "",
                Content = r.Result.Content
            })
            .ToList();

        _logger.LogInformation(
            "檢索到 {Count} 個片段：{Labels}",
            chunks.Count, string.Join("、", chunks.Select(c => c.Label)));

        chunks = Deduplicate(chunks);
        var (ordered, reordered) = ReorderCachedFirst(chunks);

        var prompt = await _promptBuilder.BuildAsync(ordered, request.Message, cancellationToken);
        var llmResult = await _llmService.GenerateFromTokensAsync(prompt.Tokens, cancellationToken);

        // 成功送出之後才記錄，避免把失敗的請求也當成已快取
        _cacheTracker.MarkSent(ordered.Select(c => c.ComposeSegmentText()));

        totalStopwatch.Stop();

        if (string.IsNullOrWhiteSpace(llmResult.Content))
        {
            throw new InvalidOperationException("推論引擎回傳空內容");
        }

        return new ChatResponse
        {
            Reply = $"📜 **法律諮詢回覆：**\n\n{llmResult.Content}\n\n{Disclaimer}",
            TtftMilliseconds = llmResult.TtftMilliseconds,
            TotalMilliseconds = totalStopwatch.ElapsedMilliseconds,
            TokenCount = llmResult.TokenCount,
            TokensPerSecond = llmResult.TokensPerSecond,
            FromCache = llmResult.FromCache,
            PromptTokens = llmResult.PromptTokens,
            CachedTokens = llmResult.CachedTokens,
            CacheHitRate = llmResult.CacheHitRate,
            RetrievedArticles = ordered.Select(c => c.Label).ToList(),
            ReorderedForCache = reordered
        };
    }

    /// <summary>
    /// 去掉內容完全相同的片段。
    ///
    /// 實測發現同一次檢索會回傳重複的條文（例如第 287 條出現兩次），
    /// 那等於浪費一個 topK 名額，而且重複的片段對快取沒有額外好處。
    /// 注意是以「片段內容」而非條號判斷——條號相同但內容不同的片段
    /// 是不同的東西，不能合併（這種情況本身是匯入時的標籤問題）。
    /// </summary>
    private List<RetrievedChunk> Deduplicate(List<RetrievedChunk> chunks)
    {
        var seen = new HashSet<string>();
        var result = new List<RetrievedChunk>(chunks.Count);

        foreach (var chunk in chunks)
        {
            if (seen.Add(chunk.ComposeSegmentText()))
            {
                result.Add(chunk);
            }
        }

        if (result.Count != chunks.Count)
        {
            _logger.LogInformation(
                "去除 {N} 個重複片段，剩餘 {Count} 段", chunks.Count - result.Count, result.Count);
        }

        return result;
    }

    /// <summary>
    /// 把「可能已在快取中」的片段排到前面。
    ///
    /// 依據：LMCache 的 lookup 一遇到未命中的片段就停止，後面的片段即使在
    /// 快取裡也不會被使用（驗證報告 8.3）。把命中的集中在前面，能讓 lookup
    /// 盡可能往後走。報告 8.3.1 的對照實驗實測為 26.9% → 67.4%。
    ///
    /// ⚠ 這是一個取捨，不是純粹的優化：
    ///   Qdrant 回傳的順序是相關度排序，最相關的片段在第一位。重新排序會
    ///   把最相關的片段往後移，而模型的答案確實會受片段位置影響——驗證報告
    ///   8.5.5 的對照組顯示，同一組條文換六種排列，條號引用有三種會出錯。
    ///
    ///   因此這個行為由 ReorderByCacheStatus 控制，預設開啟以取得效能數據，
    ///   但正確性驗收時應該兩種都跑過再決定。
    /// </summary>
    private (List<RetrievedChunk> Ordered, bool Reordered) ReorderCachedFirst(
        List<RetrievedChunk> chunks)
    {
        if (!_settings.ReorderByCacheStatus || chunks.Count < 2)
        {
            return (chunks, false);
        }

        var cached = new List<RetrievedChunk>();
        var missing = new List<RetrievedChunk>();

        foreach (var chunk in chunks)
        {
            if (_cacheTracker.IsLikelyCached(chunk.ComposeSegmentText()))
            {
                cached.Add(chunk);
            }
            else
            {
                missing.Add(chunk);
            }
        }

        // 全部命中或全部未命中時，原順序已經是最好的，不需要動
        if (cached.Count == 0 || missing.Count == 0)
        {
            return (chunks, false);
        }

        var ordered = new List<RetrievedChunk>(chunks.Count);
        ordered.AddRange(cached);     // 兩邊各自維持原本的相關度順序
        ordered.AddRange(missing);

        _logger.LogInformation(
            "依快取狀態重排：{Cached} 段可能已快取排前、{Missing} 段未快取排後 → {Order}",
            cached.Count, missing.Count, string.Join("、", ordered.Select(c => c.Label)));

        return (ordered, true);
    }
}
