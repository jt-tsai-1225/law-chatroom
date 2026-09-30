namespace ChatBot.Api.Models;

/// <summary>
/// 從 Qdrant 取回的條文片段。
///
/// Content 已包含條號那一行（由 tools/parse_pdf.py 保證），
/// 後端直接拿它當送進模型的片段文字與快取鍵，前面不得再補任何標記。
/// ArticleNumber 只作為顯示與追溯用途，不參與快取鍵。
/// </summary>
public class KnowledgeSearchResult
{
    public string Id { get; set; } = string.Empty;

    /// <summary>法典名稱，例如「公司法」。</summary>
    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
    public string Chapter { get; set; } = string.Empty;

    /// <summary>顯示標籤，例如「第 291、292、293 條」。</summary>
    public string? ArticleNumber { get; set; }

    public double Score { get; set; }
}
