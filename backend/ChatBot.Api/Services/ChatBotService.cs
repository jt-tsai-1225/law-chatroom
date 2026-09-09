using ChatBot.Api.Models;

namespace ChatBot.Api.Services;

public interface IChatBotService
{
    Task<ChatResponse> GetReplyAsync(ChatRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 法律領域聊天機器人服務 (限定民法與公司法)
/// 整合 RAG (Retrieval-Augmented Generation) 功能
/// </summary>
public class ChatBotService : IChatBotService
{
    private readonly IQdrantService _qdrantService;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILLMService _llmService;
    private readonly ILMCacheService? _cacheService;
    private readonly ICacheBlendService? _blendService;
    private readonly ILogger<ChatBotService> _logger;

    // 免責聲明
    private const string Disclaimer = "⚠️ 免責聲明：以上資訊僅供參考，不構成正式法律意見。如有具體法律問題，建議諮詢專業律師。";

    // 民法相關知識庫 (作為備用)
    private static readonly Dictionary<string, string> CivilLawKnowledgeBase = new()
    {
        // 總則編
        { "自然人", "根據民法第26條：「法人非依本法或其他法律規定，不發生人格。」第27條關於自然人的權利能力規定：「自然人因出生而取得權利能力。」自然人的權利能力從出生開始到死亡結束。" },
        { "行為能力", "民法關於行為能力的規定：\n1. 第12條：滿二十歲為成年。\n2. 第13條：未滿七歲之未成年人，無行為能力。滿七歲以上之未成年人，有限制行為能力。\n3. 未成年人已結婚者，有行為能力。" },
        { "代理", "民法第103條：「代理人於代理權限內，以本人名義所為之意思表示，直接對本人發生效力。」代理的法律效果直接歸屬於本人。" },
        { "消滅時效", "民法第125條：「請求權，因十五年間不行使而消滅。但法律所定期間較短者，依其規定。」例如：\n- 第126條：利息、紅利、租金、贍養費、退職金及其他一年或不及一年之定期給付債權，其各期給付請求權，因五年間不行使而消滅。\n- 第127條：商業營業人所為商品、產業、製造業、技工及營業所雇員之報酬等債權，因二年間不行使而消滅。" },
        
        // 債編
        { "契約", "民法第153條：「當事人互相表示意思一致者，無論其為語言或書面，契約即為成立。」契約成立的要件包括：當事人、標的物、意思一致。\n\n契約的種類包括：\n- 雙務契約與單務契約\n- 有名契約（如買賣、租賃、承攬）與無名契約" },
        { "買賣", "民法第345條：「稱買賣，謂當事人約定一方移轉財產權於他方，他方支付價金之契約。」\n\n賣方義務：\n- 移轉財產權（第348條）\n- 負擔物之瑕疵擔保責任（第354條）\n- 負擔權利瑕疵擔保責任（第350條以下）\n\n買方義務：\n- 支付價金（第367條）\n- 受領標的物（第368條）" },
        { "租賃", "民法第421條：「稱租賃者，謂當事人約定，出租人將物交付承租人使用收益，承租人支付租金之契約。」\n\n重要規定：\n- 第422條：不動產租賃之租金，应以契約訂明之。\n- 第425條：「出租人將不動產所有權讓與他人，如租賃契約之存在於前，則承租人對於新所有權人，仍可依其租賃契約，請求交付租賃物。」即『買賣不破租賃』原則。" },
        { "侵權", "民法第184條：「因故意或過失，不法侵害他人之權利者，負損害賠償責任。故意以背於善良風俗之方法，加損害於他人者亦同。違反保護他人之法律，致生損害於他人者，負損害賠償責任。但能證明其行為有致過失者，不在此限。」\n\n侵權行為的要件：\n1. 故意或過失\n2. 不法性\n3. 侵害權利\n4. 損害\n5. 因果關係" },
        { "損害賠償", "民法第213條：「負損害賠償責任者，應回復他方損害發生前之原狀。」\n\n第216條：「損害賠償，除法律另有規定或契約另有訂定外，应以填補損害為限。\n  依通常情形，或依已定之計劃、設備或其他特別情事，一定可得之利益，视为所得利益。」" },
        { "保證", "民法第739條：「稱保證者，謂當事人約定，一方他方之債務人不履行債務時，應負其責任之契約。」\n\n保證人的權利：\n- 第一順序抗辯權（第745條）\n- 分割抗辯權\n- 並行辯護權\n- 求得償權（第749條）" },
        
        // 物權編
        { "所有權", "民法第765條：「所有人，於法令限制之範圍內，得自由使用、收益、處分其所有物，並排除他人之干涉。」\n\n所有權的效力：\n- 標的物權效力\n- 排除他人干涉\n- 物上請求權（第767條）" },
        { "抵押權", "民法第860條：「稱抵押權者，謂債權人對於債務人或第三人不移轉占有而供擔保之財產，得就其賣得之價金為清償之權。」\n\n抵押權的特徵：\n- 不轉移占有\n- 從屬性\n- 不可分性\n- 物上代位性" },
        
        // 親屬編
        { "結婚", "民法第972條：「婚約，應由男女當事人自行訂定。」\n\n第983條：有下列各款情形之一者，不得結婚：\n一、未終止與前配偶之婚約者。\n二、與直系血親及直系姻親為結婚者。\n三、與八親等內之旁系血親、旁系姻親或親屬為結婚者。" },
        { "離婚", "民法第1049條：「夫妻離異，願離婚者，應以書面為之，有二人以上證人簽名，並向戶政機關辦理離婚登記。」\n\n第1052條：夫妻之一方，有下列情形之一者，他方得向法院請求離婚：\n一、重婚者。\n二、與配偶之外之人同居者。\n三、不堪同居之虐待者。\n四、對於他方之直系親屬為虐待，或受他方之直系親屬之虐待，致難以維持婚姻關係者。" },
        { "扶養", "民法第1116條之一：「直系血親相互間，對於不能維持生活者，互負扶養之義務。」\n第1117條：「受扶養權利者，以不能維持生活而無謀生能力者為限。但直系血親尊親屬，不在此限。」" },
        { "繼承", "民法第1138條：「遺產繼承人，左列之順序：\n一、直系血親卑親屬。\n二、父母。\n三、兄弟姊妹。\n四、祖父母。」\n\n第1144條：「繼承人如與被繼承人同居親屬，對於被繼承人之財產有共有關係者，就其共有財產之分割，不適用前條規定。」" },
        
        // 公司法
        { "公司設立", "公司法第98條：「有限責任公司以一人為成立之最低限制。」\n\n設立程序：\n1. 名稱核准\n2. 資本額繳納\n3. 創立大會\n4. 登記申請" },
        { "股東", "公司法第198條：「董事由股份總和超過半數之股份總數股東之同意，選舉一人以上当之。」\n\n股東權利：\n- 參加股東會\n- 表決權\n- 選舉權\n- 查帳權（第245條）" },
        { "董事", "公司法第27條：「股東會選出之董事，執行業務時，公司得由股東會決議之。」\n\n董事義務：\n- 忠實義務\n- 注意義務\n- 競業禁止（第209條）" },
        { "資本", "公司法第10條：「公司應收之股款，股東並未實際繳納，而以申專證明書或其他方法足使公眾信其已繳納者，該主管機關應處公司負責人罰鍰，並限期令該公司協請股東實際繳納；逾期仍未繳納者，即撤銷其公司登記。」" },
    };

    public ChatBotService(
        IQdrantService qdrantService,
        IEmbeddingService embeddingService,
        ILLMService llmService,
        ILogger<ChatBotService> logger,
        ILMCacheService? cacheService = null,
        ICacheBlendService? blendService = null)
    {
        _qdrantService = qdrantService;
        _embeddingService = embeddingService;
        _llmService = llmService;
        _cacheService = cacheService;
        _blendService = blendService;
        _logger = logger;
    }

    public async Task<ChatResponse> GetReplyAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        var reply = await FindReplyAsync(request.Message, cancellationToken, totalStopwatch);

        totalStopwatch.Stop();
        
        _logger.LogInformation("GetReplyAsync 完成 - 總耗時: {Total}ms", totalStopwatch.ElapsedMilliseconds);

        return new ChatResponse
        {
            Reply = reply,
            TtftMilliseconds = _lastTtftMs,
            TotalMilliseconds = totalStopwatch.ElapsedMilliseconds,
            TokenCount = _lastTokenCount,
            TokensPerSecond = _lastTokensPerSecond,
            FromCache = _lastFromCache
        };
    }
    
    // TTFT tracking fields
    private long _lastTtftMs = 0;
    private int _lastTokenCount = 0;
    private double _lastTokensPerSecond = 0;
    private bool _lastFromCache = false;

    private async Task<string> FindReplyAsync(string message, CancellationToken cancellationToken, System.Diagnostics.Stopwatch? externalStopwatch = null)
    {
        try
        {
            // 嘗試從 Qdrant 知識庫搜尋相關法律條文
            await _qdrantService.InitializeCollectionAsync();
            
            var queryVector = await _embeddingService.GenerateEmbeddingAsync(message, cancellationToken);
            var searchResults = await _qdrantService.SearchSimilarAsync("legal_documents", queryVector, topK: 5);

            if (searchResults.Any())
            {
                // 將搜尋結果組合成 context 供 LLM 使用
                var contextBuilder = new System.Text.StringBuilder();
                contextBuilder.AppendLine("以下是相關的法律條文資料，請根據這些資料回答使用者的問題：");
                contextBuilder.AppendLine();
                
                foreach (var (r, index) in searchResults.Select((result, i) => (result, i)))
                {
                    // 構建條文標題：優先顯示條文號碼，其次是章節
                    string articleTitle = !string.IsNullOrEmpty(r.Result.ArticleNumber)
                        ? r.Result.ArticleNumber
                        : (!string.IsNullOrEmpty(r.Result.Chapter) ? r.Result.Chapter : "未知名稱");
                    
                    contextBuilder.Append($"[資料 {index + 1}] ");
                    
                    if (!string.IsNullOrEmpty(r.Result.Title))
                    {
                        contextBuilder.Append($"(來自 {r.Result.Title}) ");
                    }
                    
                    contextBuilder.Append($"{articleTitle}：{r.Result.Content}");
                    contextBuilder.AppendLine();
                }
                
                // 使用 LLM 生成綜合回覆
                var systemPrompt = """
                    你是一位專業的法律諮詢助手，專門回答中華民國民法與公司法的問題。
                    
                    你的任務：
                    1. 根據提供的法律條文資料，回答使用者的問題
                    2. 保持專業、客觀、精準的態度
                    3. 如果提供的資料足夠回答，請直接回答並引用相關條文
                    4. 如果提供的資料不足，請誠實告知，並說明可能需要查詢更多資料
                    5. 回答時使用繁體中文
                    6. 不要編造法律條文內容，只根據提供的資料回答
                    7. 回答應該清晰、有條理，可以使用列表或分段的方式呈現
                    """;
                
                var userPrompt = $"""
                    使用者問題：{message}
                    
                    相關法律條文資料：
                    {contextBuilder.ToString()}
                    
                    請根據以上資料回答使用者的問題：
                    """;

                var llmResult = await _llmService.GenerateCompletionWithTimingAsync(
                    systemPrompt,
                    userPrompt,
                    cancellationToken);

                // 追蹤 TTFT 數據供 GetReplyAsync 使用
                _lastTtftMs = llmResult.TtftMilliseconds;
                _lastTokenCount = llmResult.TokenCount;
                _lastTokensPerSecond = llmResult.TokensPerSecond;
                _lastFromCache = llmResult.FromCache;

                if (!string.IsNullOrWhiteSpace(llmResult.Content))
                {
                    _logger.LogInformation(
                        "LLM 回覆生成成功 - TTFT: {Ttft}ms, 總耗時: {Total}ms, 快取命中: {FromCache}",
                        llmResult.TtftMilliseconds, llmResult.TotalMilliseconds, llmResult.FromCache);
                    return $"📜 **法律諮詢回覆：**\n\n{llmResult.Content}\n\n{Disclaimer}";
                }
            }
        }
        catch (Exception ex)
        {
            // 如果 RAG 搜尋失敗，記錄日誌並使用備用知識庫
            _logger.LogError(ex, "RAG 搜尋或 LLM 呼叫失敗，使用備用知識庫");
        }

        // 備用：使用內建知識庫
        return await FindReplyFallbackAsync(message, cancellationToken);
    }

    private async Task<string> FindReplyFallbackAsync(string message, CancellationToken cancellationToken = default)
    {
        // 嘗試從內建知識庫尋找回覆
        var lowerMessage = message.ToLowerInvariant();

        foreach (var (key, value) in CivilLawKnowledgeBase)
        {
            if (lowerMessage.Contains(key) || key.Contains(lowerMessage))
            {
                return $"📜 **法律諮詢回覆：**\n\n{value}\n\n{Disclaimer}";
            }
        }

        // 預設回覆 - 引導用戶詢問法律問題
        return $"""
            👋 **您好！我是法律諮詢助手。**

            我收到了您的問題：「{message}」

            作為一個專注於中華民國民法與公司法的法律諮詢助手，我可以在以下領域為您提供幫助：

            📚 **民法領域：**
            - 總則編：自然人、法人、期間、期間計算、代理、權利行使
            - 債編：契約、債務不履行、損害賠償、各種有名契約
            - 物權編：所有權、用益物權、擔保物權
            - 親屬編：婚姻、離婚、扶養、繼承
            - 繼承編：遺產繼承、遺囑、繼承權

            📋 **公司法領域：**
            - 公司設立與組織
            - 股東權利與義務
            - 董事與監察人
            - 資本與股份
            - 公司變更、合併與解散

            請您提出更具體的問題，我會盡力為您解答！

            {Disclaimer}
            """;
    }

}
