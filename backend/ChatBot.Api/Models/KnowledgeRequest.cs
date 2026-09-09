using System.Text.Json.Serialization;

namespace ChatBot.Api.Models;

/// <summary>
/// 新增法律條文請求模型
/// </summary>
public class AddArticleRequest
{
    /// <summary>
    /// 文件標題
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 法律條文內容
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 章節 (例如: 民法第一編)
    /// </summary>
    [JsonPropertyName("chapter")]
    public string? Chapter { get; set; }

    /// <summary>
    /// 條文號碼 (例如: 第421條)
    /// </summary>
    [JsonPropertyName("articleNumber")]
    public string? ArticleNumber { get; set; }

    /// <summary>
    /// 法律領域 (例如: "民法", "公司法")
    /// </summary>
    [JsonPropertyName("domain")]
    public string? Domain { get; set; }
}

/// <summary>
/// 知識庫上傳回應模型
/// </summary>
public class UploadKnowledgeResponse
{
    [JsonPropertyName("documents")]
    public List<DocumentInfo> Documents { get; set; } = new();

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 文件資訊
/// </summary>
public class DocumentInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("chapter")]
    public string Chapter { get; set; } = string.Empty;

    [JsonPropertyName("articleNumber")]
    public string? ArticleNumber { get; set; }

    [JsonPropertyName("contentPreview")]
    public string ContentPreview { get; set; } = string.Empty;
}

/// <summary>
/// 知識庫刪除回應模型
/// </summary>
public class DeleteKnowledgeResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
