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
    private readonly RAGSettings _settings;
    private readonly ILogger<ChatBotService> _logger;

    private const string Disclaimer =
        "⚠️ 免責聲明：以上資訊僅供參考，不構成正式法律意見。如有具體法律問題，建議諮詢專業律師。";

    public ChatBotService(
        IQdrantService qdrantService,
        IEmbeddingService embeddingService,
        IPromptBuilder promptBuilder,
        ILLMService llmService,
        IOptions<RAGSettings> settings,
        ILogger<ChatBotService> logger)
    {
        _qdrantService = qdrantService;
        _embeddingService = embeddingService;
        _promptBuilder = promptBuilder;
        _llmService = llmService;
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

        var prompt = await _promptBuilder.BuildAsync(chunks, request.Message, cancellationToken);
        var llmResult = await _llmService.GenerateFromTokensAsync(prompt.Tokens, cancellationToken);

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
            RetrievedArticles = chunks.Select(c => c.Label).ToList()
        };
    }
}
