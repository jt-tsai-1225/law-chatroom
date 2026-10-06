using ChatBot.Api.Configuration;
using ChatBot.Api.Models;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

public interface IChatBotService
{
    /// <summary>
    /// onTokenDelta：模型每生成一段文字就回呼一次（串流給前端用）。
    /// 省略時行為與以前相同——組好完整回應再一次回傳。
    /// </summary>
    Task<ChatResponse> GetReplyAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default,
        Func<string, Task>? onTokenDelta = null);
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
    private readonly IConversationStore _conversations;
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
        IConversationStore conversations,
        IOptions<RAGSettings> settings,
        ILogger<ChatBotService> logger)
    {
        _qdrantService = qdrantService;
        _embeddingService = embeddingService;
        _promptBuilder = promptBuilder;
        _llmService = llmService;
        _cacheTracker = cacheTracker;
        _conversations = conversations;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<ChatResponse> GetReplyAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default,
        Func<string, Task>? onTokenDelta = null)
    {
        try
        {
            return await GetReplyCoreAsync(request, cancellationToken, onTokenDelta);
        }
        catch
        {
            // 這一輪失敗（引擎中斷、串流中途斷線、瀏覽器關閉…）時，
            // 至少把使用者的提問留在聊天室裡。成功路徑的 PersistAsync 排在最後，
            // 中途失敗就一個字也沒存，使用者重新整理後會發現自己問過的問題消失了。
            //
            // 只存提問、不存殘缺的回答：殘缺的內容下一輪會被當成歷史送回模型，
            // 而且重新載入後看起來像一則完整的答案。
            await PersistQuestionOnlyAsync(request);
            throw;
        }
    }

    /// <summary>
    /// 只把使用者的提問寫進聊天室（回答失敗時用）。
    ///
    /// 用 CancellationToken.None：這個方法常在瀏覽器斷線、請求已被取消之後
    /// 才被呼叫，沿用原本的 token 一定會立刻失敗。
    ///
    /// 若聊天室最後一則已經是同一句提問就不再寫——使用者失敗後重試，
    /// 不該在紀錄裡留下兩則一模一樣的提問。
    /// 全程不拋例外：這是補救，不能讓補救本身蓋掉原本的錯誤。
    /// </summary>
    private async Task PersistQuestionOnlyAsync(ChatRequest request)
    {
        if (request.ConversationId is null || !_conversations.IsConfigured)
        {
            return;
        }

        var id = request.ConversationId.Value;

        try
        {
            await AppendQuestionIfNewAsync(id, request.Message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "聊天室 {Id} 的提問補寫失敗（回答本身也已失敗）", id);
        }
    }

    /// <summary>
    /// 寫入使用者的提問，但若聊天室最後一則已經是同一句提問就略過。
    ///
    /// 為什麼：回答失敗時我們補存了提問（PersistQuestionOnlyAsync），使用者
    /// 重試成功後，成功路徑又會再存一次——不去重的話紀錄裡就是兩則一模一樣
    /// 的提問，下一輪還會一起被送進模型的上下文。
    /// </summary>
    private async Task AppendQuestionIfNewAsync(Guid conversationId, string message, CancellationToken ct)
    {
        var last = await _conversations.RecentMessagesAsync(conversationId, 1, ct);
        if (last.Count > 0 && last[0].Role == "user" && last[0].Content == message)
        {
            return;
        }

        await _conversations.AppendAsync(new ConversationMessage
        {
            ConversationId = conversationId,
            Role = "user",
            Content = message
        }, ct);
    }

    private async Task<ChatResponse> GetReplyCoreAsync(
        ChatRequest request,
        CancellationToken cancellationToken,
        Func<string, Task>? onTokenDelta)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        await _qdrantService.InitializeCollectionAsync();

        // 歷史要在檢索之前就讀出來：追問（「那如果房東不退還呢？」）單看最後
        // 一句話檢索不到相關條文，需要把上一個提問一起納入。
        var history = await LoadHistoryAsync(request.ConversationId, cancellationToken);

        var (searchResults, expandedByHistory) = await RetrieveAsync(
            request.Message, history, cancellationToken);

        if (searchResults.Count == 0)
        {
            totalStopwatch.Stop();
            _logger.LogWarning("知識庫沒有檢索到任何條文，問題：{Message}", request.Message);

            // 這一輪也要進聊天室。少了它，使用者回頭看紀錄會發現自己
            // 問過的問題憑空消失，以為是系統吃掉了訊息。
            var empty = new ChatResponse
            {
                Reply = $"查無相關條文，無法回答這個問題。\n\n{Disclaimer}",
                RawReply = "查無相關條文，無法回答這個問題。",
                TotalMilliseconds = totalStopwatch.ElapsedMilliseconds,
                ConversationId = request.ConversationId,
                CacheMode = CacheModes.Normalize(request.CacheMode)
            };

            empty.Persisted = await PersistAsync(
                request, empty, "查無相關條文，無法回答這個問題。", cancellationToken);

            return empty;
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

        // 第 ③ 步：決定片段順序。
        // 正常情況走 ReorderCachedFirst；其餘模式是驗證用的明確排列，
        // 只影響順序、不影響片段組合，因此可以乾淨地隔離「順序」這個變因。
        var mode = ChunkOrderStrategy.Normalize(request.ChunkOrder);
        var seed = request.ChunkOrderSeed ?? ChunkOrderStrategy.DefaultSeed;

        List<RetrievedChunk> ordered;
        bool reordered;
        List<int> permutation;
        int? seedUsed = null;

        if (ChunkOrderStrategy.IsExplicit(mode))
        {
            permutation = ChunkOrderStrategy.BuildPermutation(mode, chunks.Count, seed);
            ordered = permutation.Select(i => chunks[i]).ToList();
            reordered = false;

            if (mode == ChunkOrderStrategy.Shuffle)
            {
                seedUsed = seed;
            }

            _logger.LogInformation(
                "片段排列 {Mode}{Seed}：[{Perm}] → {Order}",
                mode,
                seedUsed is null ? "" : $"（seed={seedUsed}）",
                string.Join(",", permutation),
                string.Join("、", ordered.Select(c => c.Label)));
        }
        else
        {
            (ordered, reordered) = ReorderCachedFirst(chunks);

            // 依快取狀態重排是比對內容決定的，排列表只能從結果反推
            permutation = ordered
                .Select(c => chunks.FindIndex(x => ReferenceEquals(x, c)))
                .ToList();
        }

        var prompt = await _promptBuilder.BuildAsync(
            ordered, request.Message, history, cancellationToken);

        // 注意：上面已經有一個 mode 是片段排列模式，兩者不同，別混用
        var cacheMode = CacheModes.Normalize(request.CacheMode);
        var llmResult = await _llmService.GenerateFromTokensAsync(
            prompt.Tokens, cancellationToken, cacheMode: cacheMode, onTokenDelta: onTokenDelta);

        // 純前綴快取在本次請求能命中的上限。缺少這個對照，
        // 「命中率 99.98%」無法區分究竟是 blend 生效還是單純的前綴複用。
        var prefixCeiling = ChunkOrderStrategy.PrefixOnlyCeiling(
            permutation, prompt.ChunkOffsets, prompt.Tokens.Length);

        // 成功送出之後才記錄，避免把失敗的請求也當成已快取
        _cacheTracker.MarkSent(ordered.Select(c => c.ComposeSegmentText()));

        totalStopwatch.Stop();

        if (string.IsNullOrWhiteSpace(llmResult.Content))
        {
            throw new InvalidOperationException("推論引擎回傳空內容");
        }

        var response = new ChatResponse
        {
            Reply = $"📜 **法律諮詢回覆：**\n\n{llmResult.Content}\n\n{Disclaimer}",
            RawReply = llmResult.Content,
            TtftMilliseconds = llmResult.TtftMilliseconds,
            TotalMilliseconds = totalStopwatch.ElapsedMilliseconds,
            TokenCount = llmResult.TokenCount,
            TokensPerSecond = llmResult.TokensPerSecond,
            FromCache = llmResult.FromCache,
            PromptTokens = llmResult.PromptTokens,
            CachedTokens = llmResult.CachedTokens,
            CacheHitRate = llmResult.CacheHitRate,
            RetrievedArticles = ordered.Select(c => c.Label).ToList(),
            ReorderedForCache = reordered,
            ChunkOrderApplied = mode,
            ChunkPermutation = permutation,
            ChunkOrderSeedUsed = seedUsed,
            PrefixOnlyCeilingTokens = prefixCeiling,
            CachedOverPrefixCeiling = prefixCeiling > 0
                ? Math.Round((double)llmResult.CachedTokens / prefixCeiling, 2)
                : 0,
            ConversationId = request.ConversationId,
            HistoryMessagesUsed = prompt.HistoryMessagesUsed,
            RetrievalExpandedByHistory = expandedByHistory,
            CacheMode = cacheMode
        };

        response.Persisted = await PersistAsync(
            request, response, llmResult.Content, cancellationToken);

        return response;
    }

    /// <summary>
    /// 檢索條文。有歷史時多做一次「上一個提問＋本次提問」的檢索並合併。
    ///
    /// 合併規則：同一個片段取較高的分數，依分數由高到低取前 topK。
    /// 兩次檢索用的是同一個集合與同一個向量空間，分數可以直接比較。
    ///
    /// 沒有歷史（或設定關閉）時只做原本那一次，行為、延遲與以前完全相同。
    /// 第二次的 embedding 與第一次平行發出，不會讓追問多等一次來回。
    /// </summary>
    private async Task<(List<(KnowledgeSearchResult Result, double Score)> Results, bool Expanded)>
        RetrieveAsync(string message, List<ConversationTurn>? history, CancellationToken ct)
    {
        const string collection = "legal_documents";
        var topK = _settings.RetrievalTopK;

        var previousQuestions = _settings.RetrievalHistoryUserTurns > 0 && history is not null
            ? history
                .Where(t => t.Role == "user" && !string.IsNullOrWhiteSpace(t.Content))
                .TakeLast(_settings.RetrievalHistoryUserTurns)
                .Select(t => t.Content)
                .ToList()
            : new List<string>();

        if (previousQuestions.Count == 0)
        {
            var vector = await _embeddingService.GenerateEmbeddingAsync(message, ct);
            var single = await _qdrantService.SearchSimilarAsync(collection, vector, topK: topK);
            return (single, false);
        }

        var combinedText = string.Join("\n", previousQuestions) + "\n" + message;

        var plainTask = _embeddingService.GenerateEmbeddingAsync(message, ct);
        var combinedTask = _embeddingService.GenerateEmbeddingAsync(combinedText, ct);
        await Task.WhenAll(plainTask, combinedTask);

        var plain = await _qdrantService.SearchSimilarAsync(collection, plainTask.Result, topK: topK);
        var combined = await _qdrantService.SearchSimilarAsync(collection, combinedTask.Result, topK: topK);

        var merged = plain.Concat(combined)
            .GroupBy(r => r.Result.Id)
            .Select(g => g.OrderByDescending(r => r.Score).First())
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();

        var plainIds = new HashSet<string>(plain.Select(r => r.Result.Id));
        var addedByHistory = merged.Count(r => !plainIds.Contains(r.Result.Id));

        _logger.LogInformation(
            "追問檢索：單句 {Plain} 筆、含上一提問 {Combined} 筆，合併取 {Merged} 筆，其中 {Added} 筆是靠上一提問才找到",
            plain.Count, combined.Count, merged.Count, addedByHistory);

        return (merged, addedByHistory > 0);
    }

    /// <summary>
    /// 取出要帶進 prompt 的對話歷史。
    ///
    /// 沒有指定聊天室、或資料庫沒設定時回 null——那等於單輪問答，
    /// 與加入聊天室功能之前的行為完全相同。
    ///
    /// 讀取失敗不讓問答失敗：少了上下文的回答仍然有用，
    /// 比起因為讀不到歷史而整個請求失敗好得多。
    /// </summary>
    private async Task<List<ConversationTurn>?> LoadHistoryAsync(
        Guid? conversationId, CancellationToken ct)
    {
        if (conversationId is null
            || !_conversations.IsConfigured
            || _settings.MaxHistoryMessages <= 0)
        {
            return null;
        }

        try
        {
            var messages = await _conversations.RecentMessagesAsync(
                conversationId.Value, _settings.MaxHistoryMessages, ct);

            return messages
                .Select(m => new ConversationTurn(m.Role, m.Content))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "讀取聊天室 {Id} 的歷史失敗，本次以單輪方式回答", conversationId);
            return null;
        }
    }

    /// <summary>
    /// 把這一輪的提問與回答寫進聊天室。
    ///
    /// 存的是**模型原始輸出**（rawReply），不是 response.Reply——後者帶著
    /// 「法律諮詢回覆」標題與免責聲明，那是呈現用的裝飾，存進去會在下一輪
    /// 被當成上下文送回模型。
    ///
    /// 回傳是否成功。失敗只記錄不拋出：使用者已經拿到回答了。
    /// </summary>
    private async Task<bool> PersistAsync(
        ChatRequest request, ChatResponse response, string rawReply, CancellationToken ct)
    {
        if (request.ConversationId is null || !_conversations.IsConfigured)
        {
            return false;
        }

        var id = request.ConversationId.Value;

        try
        {
            await AppendQuestionIfNewAsync(id, request.Message, ct);

            await _conversations.AppendAsync(new ConversationMessage
            {
                ConversationId = id,
                Role = "assistant",
                Content = rawReply,
                CacheMode = response.CacheMode,
                ChunkOrder = response.ChunkOrderApplied,
                TtftMs = (int)response.TtftMilliseconds,
                TotalMs = (int)response.TotalMilliseconds,
                PromptTokens = response.PromptTokens,
                CachedTokens = response.CachedTokens,
                CacheHitRate = response.CacheHitRate,
                PrefixCeilingTokens = response.PrefixOnlyCeilingTokens,
                CachedOverPrefixCeiling = response.CachedOverPrefixCeiling,
                RetrievedArticles = response.RetrievedArticles
            }, ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "聊天室 {Id} 的訊息寫入失敗。回答已經產生，僅未保存", id);
            return false;
        }
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
