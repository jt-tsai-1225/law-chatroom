namespace ChatBot.Api.Models;

/// <summary>
/// 知識庫文件模型
/// </summary>
public class KnowledgeDocument
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Chapter { get; set; } = string.Empty;  // 例如: 債編、物權編
    public string? ArticleNumber { get; set; }  // 例如: 第345條
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public string SourceFile { get; set; } = string.Empty;
}

/// <summary>
/// 知識庫搜尋結果
/// </summary>
public class KnowledgeSearchResult
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Chapter { get; set; } = string.Empty;
    public string? ArticleNumber { get; set; }
    public double Score { get; set; }
}

/// <summary>
/// 上傳文件請求
/// </summary>
public class UploadDocumentRequest
{
    public IFormFile? File { get; set; }
    public string? Title { get; set; }
    public string? Chapter { get; set; }
}
