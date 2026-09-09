using ChatBot.Api.Models;

namespace ChatBot.Api.Services;

/// <summary>
/// PDF 解析器介面
/// </summary>
public interface IPDFParser
{
    /// <summary>
    /// 從 PDF 檔案解析文字內容
    /// </summary>
    /// <param name="filePath">PDF 檔案路徑</param>
    /// <returns>解析後的文字內容</returns>
    Task<List<KnowledgeDocument>> ParsePdfAsync(string filePath);

    /// <summary>
    /// 從 PDF 文字區塊提取法律條文結構
    /// </summary>
    /// <param name="content">PDF 原始文字內容</param>
    /// <param name="title">文件標題</param>
    /// <returns>結構化的法律條文列表</returns>
    List<KnowledgeDocument> ExtractLegalArticles(string content, string title);
}
