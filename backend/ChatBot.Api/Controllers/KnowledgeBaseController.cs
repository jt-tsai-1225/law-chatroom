using ChatBot.Api.Models;
using ChatBot.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Api.Controllers;

/// <summary>
/// 知識庫管理 API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class KnowledgeBaseController : ControllerBase
{
    private readonly IQdrantService _qdrantService;
    private readonly IPDFParser _pdfParser;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<KnowledgeBaseController> _logger;
    private readonly string _uploadPath;

    public KnowledgeBaseController(
        IQdrantService qdrantService,
        IPDFParser pdfParser,
        IEmbeddingService embeddingService,
        ILogger<KnowledgeBaseController> logger,
        IWebHostEnvironment environment)
    {
        _qdrantService = qdrantService;
        _pdfParser = pdfParser;
        _embeddingService = embeddingService;
        _logger = logger;
        _uploadPath = Path.Combine(environment.ContentRootPath, "uploads");
        
        // 確保上傳目錄存在
        if (!Directory.Exists(_uploadPath))
        {
            Directory.CreateDirectory(_uploadPath);
        }
    }

    /// <summary>
    /// 新增法律條文到知識庫 (支援文字輸入)
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> AddArticle([FromBody] AddArticleRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.Title))
            {
                return BadRequest(new { message = "請提供標題" });
            }

            if (string.IsNullOrEmpty(request.Content))
            {
                return BadRequest(new { message = "請提供內容" });
            }

            // 初始化 Qdrant 集合
            await _qdrantService.InitializeCollectionAsync();

            // 解析法律條文結構
            var documents = _pdfParser.ExtractLegalArticles(request.Content, request.Title);

            // 如果沒有解析出結構化數據，創建一個默認文檔
            if (documents.Count == 0)
            {
                documents.Add(new KnowledgeDocument
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = request.Title,
                    Content = request.Content,
                    Chapter = request.Chapter ?? "其他",
                    ArticleNumber = request.ArticleNumber,
                    UploadedAt = DateTime.UtcNow
                });
            }

            // 向量化並插入 Qdrant
            var createdDocuments = new List<DocumentInfo>();

            foreach (var doc in documents)
            {
                // 生成向量
                var embedding = await _embeddingService.GenerateEmbeddingAsync(doc.Content);

                // 準備 payload
                var payload = new KeyValuePair<string, string>[]
                {
                    new("title", doc.Title),
                    new("content", doc.Content),
                    new("chapter", doc.Chapter),
                    new("articleNumber", doc.ArticleNumber ?? ""),
                    new("domain", request.Domain ?? ""),
                    new("uploadedAt", doc.UploadedAt.ToString("o"))
                };

                // 插入 Qdrant
                await _qdrantService.UpsertDocumentAsync("legal_documents", doc.Id, embedding, payload);

                // 準備回應資訊
                createdDocuments.Add(new DocumentInfo
                {
                    Id = doc.Id,
                    Title = doc.Title,
                    Chapter = doc.Chapter,
                    ArticleNumber = doc.ArticleNumber,
                    ContentPreview = doc.Content.Length > 100 ? doc.Content.Substring(0, 100) + "..." : doc.Content
                });
            }

            _logger.LogInformation("成功新增 {Count} 個法律條文到知識庫", createdDocuments.Count);

            return Ok(new UploadKnowledgeResponse
            {
                Documents = createdDocuments,
                Message = $"成功新增 {createdDocuments.Count} 個法律條文"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "新增法律條文失敗");
            return StatusCode(500, new { message = "新增失敗", error = ex.Message });
        }
    }

    /// <summary>
    /// 列出知識庫中的所有文件
    /// 注意：此功能尚未實作。目前沒有維護獨立的文件索引，
    /// 無法列出 Qdrant 集合中的全部內容，請改用 /api/knowledgebase/search 依關鍵字查詢。
    /// </summary>
    [HttpGet("documents")]
    public IActionResult GetDocuments([FromQuery] string? domain = null)
    {
        return StatusCode(501, new
        {
            documents = Array.Empty<KnowledgeDocument>(),
            count = 0,
            message = "列出全部文件功能尚未實作，請改用 GET /api/knowledgebase/search?query=... 查詢知識庫內容"
        });
    }

    /// <summary>
    /// 刪除知識庫中的文件
    /// </summary>
    [HttpDelete("documents/{id}")]
    public async Task<IActionResult> DeleteDocument(string id)
    {
        try
        {
            await _qdrantService.InitializeCollectionAsync();
            await _qdrantService.DeleteDocumentAsync("legal_documents", id);

            _logger.LogInformation("成功刪除文件: {Id}", id);

            return Ok(new DeleteKnowledgeResponse
            {
                Success = true,
                Message = $"成功刪除文件 {id}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刪除知識庫文件失敗: {Id}", id);
            return StatusCode(500, new DeleteKnowledgeResponse
            {
                Success = false,
                Message = $"刪除失敗: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// 搜尋知識庫
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> SearchKnowledgeBase([FromQuery] string query, [FromQuery] int topK = 5)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return BadRequest(new { message = "請提供搜尋關鍵字" });
            }

            _logger.LogInformation("[KnowledgeBaseController] 收到搜尋請求 - query: {Query}, topK: {TopK}", query, topK);

            await _qdrantService.InitializeCollectionAsync();

            // 生成查詢向量
            var queryVector = await _embeddingService.GenerateEmbeddingAsync(query);
            _logger.LogInformation("[KnowledgeBaseController] 生成查詢向量完成 - 維度: {Dimensions}", queryVector.Length);

            // 搜尋相似條文
            var results = await _qdrantService.SearchSimilarAsync("legal_documents", queryVector, topK);

            _logger.LogInformation("[KnowledgeBaseController] Qdrant 回傳 {ResultCount} 個結果", results.Count);

            var searchResults = results.Select(r => new
            {
                r.Result.Id,
                r.Result.Title,
                r.Result.Content,
                r.Result.Chapter,
                r.Result.ArticleNumber,
                score = r.Score
            }).ToList();

            _logger.LogInformation("[KnowledgeBaseController] 最終回傳 {FinalCount} 個結果", searchResults.Count);

            return Ok(new { query, results = searchResults, count = searchResults.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜尋知識庫失敗");
            return StatusCode(500, new { message = "搜尋失敗", error = ex.Message });
        }
    }

    /// <summary>
    /// 清空知識庫集合
    /// </summary>
    [HttpDelete("clear")]
    public async Task<IActionResult> ClearKnowledgeBase()
    {
        try
        {
            await _qdrantService.InitializeCollectionAsync();
            await _qdrantService.TruncateCollectionAsync("legal_documents");

            _logger.LogInformation("成功清空知識庫");

            return Ok(new { message = "成功清空知識庫" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清空知識庫失敗");
            return StatusCode(500, new { message = "清空失敗", error = ex.Message });
        }
    }
}
