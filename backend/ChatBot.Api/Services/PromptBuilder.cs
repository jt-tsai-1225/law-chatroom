using ChatBot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

/// <summary>檢索回來、要放進 prompt 的一個條文片段。</summary>
public sealed class RetrievedChunk
{
    /// <summary>條號，例如「第二百九十三條」。Qdrant 把它與內文分開存放。</summary>
    public string ArticleNumber { get; init; } = "";

    /// <summary>條文內文（不含條號那一行）。</summary>
    public string Content { get; init; } = "";

    /// <summary>
    /// 片段的 token id。日後匯入時預先算好存進 Qdrant payload 就直接帶進來；
    /// 目前留 null，由 PromptBuilder 呼叫 /tokenize 補上（結果會被快取）。
    /// </summary>
    public int[]? Tokens { get; init; }

    /// <summary>來源標示，僅用於記錄與警告訊息。</summary>
    public string Label => string.IsNullOrEmpty(ArticleNumber) ? "(無條號)" : ArticleNumber;

    /// <summary>
    /// 實際送進模型的片段文字。
    ///
    /// ★ 這就是快取鍵的來源，必須與 Qdrant 中儲存的內容逐字相同 ★
    /// CacheBlend 以片段的 token id 內容雜湊作為鍵。只要這裡多一個空白、
    /// 換行位置不同，或加上「[資料 1]」這種**隨位置改變**的標記，
    /// 同一條條文在不同次檢索就會算出不同的鍵，永遠不會命中。
    ///
    /// 因此這裡直接回傳 Content，不做任何加工——匯入腳本
    /// （import_legal_pdfs.py 的 PDFParser）已經把條號那一行放進內容裡，
    /// 片段本身就帶著「第 293 條」，模型引用得出來，不需要外部再補標籤。
    /// ArticleNumber 只作為顯示與追溯用途，不參與快取鍵。
    /// </summary>
    public string ComposeSegmentText() => Content;
}

public sealed class BuiltPrompt
{
    public required int[] Tokens { get; init; }
    public required int ChunkCount { get; init; }

    /// <summary>各片段在 prompt 中的起始位置（不含分隔符），供除錯與命中率驗證使用。</summary>
    public required IReadOnlyList<int> ChunkOffsets { get; init; }

    /// <summary>各片段的 token 數，順序與 ChunkOffsets 相同。</summary>
    public required IReadOnlyList<int> ChunkTokenCounts { get; init; }

    /// <summary>prompt 開頭的固定部分（BOS + [INST] + 系統提示詞）的 token 數。</summary>
    public required int PrefixTokens { get; init; }

    /// <summary>實際帶進 prompt 的歷史訊息則數（可能因長度上限而少於傳入的數量）。</summary>
    public int HistoryMessagesUsed { get; init; }
}

/// <summary>對話歷史裡的一則訊息。只有角色與內容，不帶量測資料。</summary>
public sealed record ConversationTurn(string Role, string Content);

/// <summary>
/// 依 CacheBlend 的要求組出 token id 陣列。
///
/// 結構（Mistral-7B-Instruct 的 [INST] 格式）：
///   [1, 733, 16289, 28793]           BOS + [INST]
///   + 系統提示詞
///   + (分隔符 + 條文片段) × N
///   + 分隔符 + 對話歷史 + 使用者問題
///   + [733, 28748, 16289, 28793]     [/INST]
///
/// ★ 對話歷史一定要放在條文片段「之後」★
///   放在前面的話，每一輪歷史變長都會把所有條文的位置往後推：
///   前綴快取從第一個條文就全盤失效，而我們正是靠前綴快取當對照組。
///   放在後面則條文的位置與內容都不變，兩種快取都照常運作。
///
///   歷史與問題合併在同一個片段，不另外切一段——那一段本來就每次都不同、
///   永遠不會命中，再多切一刀只是增加片段數。
///
/// 兩個硬性要求（來源：CacheBlend 驗證報告 12.5）：
///   1. 分隔符必須以 token id 插入（本模型為 [422, 422]），不可用字串串接。
///      字串串接會讓分隔符與相鄰文字合併成不同的 token，片段邊界就對不上。
///   2. 片段不宜過短。注意：LMCache 的 blend_min_tokens 是未實作的設定，
///      並不存在「低於門檻就不走 blend」這回事（見 RAGSettings.ChunkWarnMinTokens）。
///      過短的真正代價是片段數變多，而 lookup 遇到第一個未命中片段就會停止。
/// </summary>
public interface IPromptBuilder
{
    Task<BuiltPrompt> BuildAsync(
        IReadOnlyList<RetrievedChunk> chunks,
        string question,
        IReadOnlyList<ConversationTurn>? history = null,
        CancellationToken cancellationToken = default);
}

public class PromptBuilder : IPromptBuilder
{
    private readonly ITokenizerClient _tokenizer;
    private readonly RAGSettings _settings;
    private readonly ILogger<PromptBuilder> _logger;

    public PromptBuilder(
        ITokenizerClient tokenizer,
        IOptions<RAGSettings> settings,
        ILogger<PromptBuilder> logger)
    {
        _tokenizer = tokenizer;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<BuiltPrompt> BuildAsync(
        IReadOnlyList<RetrievedChunk> chunks,
        string question,
        IReadOnlyList<ConversationTurn>? history = null,
        CancellationToken cancellationToken = default)
    {
        var separator = await _tokenizer.TokenizeCachedAsync(
            _settings.BlendSeparator, false, cancellationToken);

        if (separator.Length == 0)
        {
            throw new InvalidOperationException(
                $"分隔符 '{_settings.BlendSeparator}' tokenize 後為空，請檢查 BlendSeparator 設定");
        }

        var systemTokens = await _tokenizer.TokenizeCachedAsync(
            _settings.SystemPrompt, false, cancellationToken);

        var ids = new List<int>(capacity: 8192);
        ids.AddRange(_settings.InstPrefixTokens);
        ids.AddRange(systemTokens);

        var offsets = new List<int>(chunks.Count);
        var counts = new List<int>(chunks.Count);
        var prefixTokens = ids.Count;

        foreach (var chunk in chunks)
        {
            var chunkTokens = chunk.Tokens
                ?? await _tokenizer.TokenizeCachedAsync(
                        chunk.ComposeSegmentText(), false, cancellationToken);

            if (chunkTokens.Length < _settings.ChunkWarnMinTokens)
            {
                // 這只是提醒，不代表該片段不會被快取——短片段一樣會。
                // 問題在於片段數變多後，lookup 更容易在前面踩到未命中而中斷。
                _logger.LogWarning(
                    "片段「{Label}」只有 {Len} tokens（低於 {Min}）。短片段會使片段數增加，" +
                    "而 lookup 遇到第一個未命中片段即停止，命中率因此更容易受損。" +
                    "可考慮在匯入時與鄰近條文合併",
                    chunk.Label, chunkTokens.Length, _settings.ChunkWarnMinTokens);
            }

            ids.AddRange(separator);
            offsets.Add(ids.Count);
            counts.Add(chunkTokens.Length);
            ids.AddRange(chunkTokens);
        }

        // 歷史的裁切只有這一個來源：先依則數上限截斷，再視 token 總量
        // 從最舊的開始丟。保留最近的輪次比保留最早的有用。
        var turns = (history ?? Array.Empty<ConversationTurn>())
            .Where(t => !string.IsNullOrWhiteSpace(t.Content))
            .ToList();

        if (turns.Count > _settings.MaxHistoryMessages)
        {
            turns = turns.Skip(turns.Count - _settings.MaxHistoryMessages).ToList();
        }

        var budget = _settings.MaxPromptTokens - _settings.InstSuffixTokens.Length;
        string tail;
        int[] tailTokens;

        while (true)
        {
            tail = ComposeTail(turns, question);
            tailTokens = await _tokenizer.TokenizeAsync(tail, false, cancellationToken);

            if (turns.Count == 0 ||
                ids.Count + separator.Length + tailTokens.Length <= budget)
            {
                break;
            }

            turns.RemoveAt(0);
            _logger.LogWarning(
                "對話歷史使 prompt 超過 {Budget} tokens，捨去最早的一則，剩 {N} 則",
                budget, turns.Count);
        }

        var historyTurnsUsed = turns.Count;

        ids.AddRange(separator);
        ids.AddRange(tailTokens);
        ids.AddRange(_settings.InstSuffixTokens);

        _logger.LogInformation(
            "組出 prompt：{Total} tokens（系統 {Sys} + {N} 個片段 + 歷史 {H} 則 + 問題與歷史合計 {Q}）",
            ids.Count, systemTokens.Length, chunks.Count, historyTurnsUsed, tailTokens.Length);

        return new BuiltPrompt
        {
            Tokens = ids.ToArray(),
            ChunkCount = chunks.Count,
            ChunkOffsets = offsets,
            ChunkTokenCounts = counts,
            PrefixTokens = prefixTokens,
            HistoryMessagesUsed = historyTurnsUsed
        };
    }

    /// <summary>
    /// 組出 prompt 的尾段：對話歷史 + 本次問題。
    ///
    /// 助理的訊息存進資料庫時已經去掉「法律諮詢回覆」標題與免責聲明，
    /// 這裡拿到的是模型原始輸出——那兩段是呈現用的裝飾，送回模型只是
    /// 浪費 token 並擾亂上下文。
    /// </summary>
    private static string ComposeTail(IReadOnlyList<ConversationTurn> turns, string question)
    {
        if (turns.Count == 0)
        {
            return question;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("以下是先前的對話，供理解本次問題的脈絡：");
        foreach (var t in turns)
        {
            var who = t.Role == "assistant" ? "助理" : "使用者";
            sb.Append(who).Append('：').AppendLine(t.Content.Trim());
        }
        sb.AppendLine();
        sb.Append("本次問題：").Append(question);

        return sb.ToString();
    }
}
