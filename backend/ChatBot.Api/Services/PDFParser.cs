using ChatBot.Api.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace ChatBot.Api.Services;

/// <summary>
/// PDF 解析器實作
/// 目前使用簡化的文字輸入方式
/// PDF 解析功能需要額外安裝 PDF 處理庫
/// </summary>
public class PDFParser : IPDFParser
{
    private readonly ILogger<PDFParser> _logger;

    public PDFParser(ILogger<PDFParser> logger)
    {
        _logger = logger;
    }

    public async Task<List<KnowledgeDocument>> ParsePdfAsync(string filePath)
    {
        _logger.LogWarning("PDF 解析功能尚未實作，請使用文字輸入方式添加法律條文");
        return new List<KnowledgeDocument>();
    }

    public List<KnowledgeDocument> ExtractLegalArticles(string content, string title)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new List<KnowledgeDocument>();
        }

        var documents = new List<KnowledgeDocument>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // 法律條文正則表達式。
        //
        // 必須整行比對（^...$），否則會把內文裡的**交叉引用**誤判為新條文的開始：
        //   「準用第二十九條第一項規定」「公司有第一百五十六條之四之情形者」
        // 這些都不是條號，卻會讓片段在句子中間被切開，並被標上錯誤的條號。
        //
        // 另外條號實際的寫法是阿拉伯數字加空格（「第 1 條」「第 319-1 條」），
        // 只認中文數字會導致真正的條號一個都抓不到。
        // 詳見 import_legal_pdfs.py 中 PDFParser 的說明。
        var articlePattern = @"^第\s*(\d+(?:\s*-\s*\d+)?|[零一二三四五六七八九十百千○]+)\s*[條条]\s*$";
        var chapterPattern = @"^第\s*[\d零一二三四五六七八九十百千○]+\s*[章]";
        var sectionPattern = @"^第\s*[\d零一二三四五六七八九十百千○]+\s*[節]";
        
        var currentArticle = string.Empty;
        var currentContent = new StringBuilder();
        var currentChapter = string.Empty;
        var currentSection = string.Empty;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // 偵測節
            if (Regex.IsMatch(trimmedLine, sectionPattern))
            {
                // 保存前一個條文
                if (!string.IsNullOrEmpty(currentArticle) && currentContent.Length > 0)
                {
                    documents.Add(CreateDocument(title, currentChapter, currentSection, currentArticle, currentContent.ToString()));
                }
                currentSection = trimmedLine;
                currentArticle = string.Empty;
                currentContent.Clear();
                continue;
            }

            // 偵測章節
            if (Regex.IsMatch(trimmedLine, chapterPattern))
            {
                // 保存前一個條文
                if (!string.IsNullOrEmpty(currentArticle) && currentContent.Length > 0)
                {
                    documents.Add(CreateDocument(title, currentChapter, currentSection, currentArticle, currentContent.ToString()));
                }

                currentChapter = trimmedLine;
                currentSection = string.Empty;
                currentArticle = string.Empty;
                currentContent.Clear();
                continue;
            }

            // 偵測條文
            if (Regex.IsMatch(trimmedLine, articlePattern))
            {
                // 保存前一個條文
                if (!string.IsNullOrEmpty(currentArticle) && currentContent.Length > 0)
                {
                    documents.Add(CreateDocument(title, currentChapter, currentSection, currentArticle, currentContent.ToString()));
                }

                currentArticle = trimmedLine;
                currentContent.Clear();
                continue;
            }

            // 內容行
            if (!string.IsNullOrEmpty(currentArticle))
            {
                currentContent.AppendLine(trimmedLine);
            }
        }

        // 保存最後一個條文
        if (!string.IsNullOrEmpty(currentArticle) && currentContent.Length > 0)
        {
            documents.Add(CreateDocument(title, currentChapter, currentSection, currentArticle, currentContent.ToString()));
        }

        // 如果沒有找到結構化的條文，将整个文档作为一个片段
        if (documents.Count == 0 && !string.IsNullOrEmpty(content))
        {
            documents.Add(new KnowledgeDocument
            {
                Id = Guid.NewGuid().ToString(),
                Title = title,
                Content = content,
                Chapter = "全文",
                ArticleNumber = null,
                UploadedAt = DateTime.UtcNow
            });
        }

        return documents;
    }

    private KnowledgeDocument CreateDocument(string title, string chapter, string section, string article, string content)
    {
        // 提取條文號碼 (例如 "第一條" -> "第一條")
        var match = Regex.Match(article, @"(第[零一二三四五六七八九十百千万○]+[條条])");
        var articleNumber = match.Success ? match.Value : article;

        return new KnowledgeDocument
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            Chapter = !string.IsNullOrEmpty(section) ? $"{chapter}/{section}" : chapter,
            ArticleNumber = articleNumber,
            Content = content.ToString().Trim(),
            UploadedAt = DateTime.UtcNow
        };
    }
}
