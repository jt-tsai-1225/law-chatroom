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
}

/// <summary>
/// 依 CacheBlend 的要求組出 token id 陣列。
///
/// 結構（Mistral-7B-Instruct 的 [INST] 格式）：
///   [1, 733, 16289, 28793]           BOS + [INST]
///   + 系統提示詞
///   + (分隔符 + 條文片段) × N
///   + 分隔符 + 使用者問題
///   + [733, 28748, 16289, 28793]     [/INST]
///
/// 兩個硬性要求（來源：CacheBlend 驗證報告 12.5）：
///   1. 分隔符必須以 token id 插入（本模型為 [422, 422]），不可用字串串接。
///      字串串接會讓分隔符與相鄰文字合併成不同的 token，片段邊界就對不上。
///   2. 每個片段長度需大於 blend_min_tokens（預設 256），否則該片段不走 blend。
/// </summary>
public interface IPromptBuilder
{
    Task<BuiltPrompt> BuildAsync(
        IReadOnlyList<RetrievedChunk> chunks,
        string question,
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

        foreach (var chunk in chunks)
        {
            var chunkTokens = chunk.Tokens
                ?? await _tokenizer.TokenizeCachedAsync(
                        chunk.ComposeSegmentText(), false, cancellationToken);

            if (chunkTokens.Length < _settings.BlendMinTokens)
            {
                _logger.LogWarning(
                    "片段「{Label}」只有 {Len} tokens，低於 blend_min_tokens={Min}，" +
                    "這一段不會走 blend（匯入時的分組顆粒度可能過細）",
                    chunk.Label, chunkTokens.Length, _settings.BlendMinTokens);
            }

            ids.AddRange(separator);
            offsets.Add(ids.Count);
            ids.AddRange(chunkTokens);
        }

        var questionTokens = await _tokenizer.TokenizeAsync(question, false, cancellationToken);
        ids.AddRange(separator);
        ids.AddRange(questionTokens);
        ids.AddRange(_settings.InstSuffixTokens);

        _logger.LogInformation(
            "組出 prompt：{Total} tokens（系統 {Sys} + {N} 個片段 + 問題 {Q}）",
            ids.Count, systemTokens.Length, chunks.Count, questionTokens.Length);

        return new BuiltPrompt
        {
            Tokens = ids.ToArray(),
            ChunkCount = chunks.Count,
            ChunkOffsets = offsets
        };
    }
}
