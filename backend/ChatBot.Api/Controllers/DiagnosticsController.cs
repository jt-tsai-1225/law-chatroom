using System.Text.RegularExpressions;
using ChatBot.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Api.Controllers;

public class ProbeRequest
{
    /// <summary>使用者問句，會放在最後一個分隔符之後。</summary>
    public string Question { get; set; } = "";

    /// <summary>
    /// 要放進 prompt 的條號，**順序即 prompt 中的順序**。
    /// 例如 ["293", "305", "310"] 會依序帶入包含這些條文的片段。
    /// </summary>
    public List<string> Articles { get; set; } = new();
}

/// <summary>
/// 診斷端點：手動指定片段與順序，用來驗證 CacheBlend 是否真的在做事。
///
/// 為什麼需要這個：
///   「同一個 prompt 送兩次命中」不是 CacheBlend 的證據——一般前綴快取即可達成。
///   CacheBlend 的價值只在片段**跨 prompt、落到不同位置**時才成立。
///   靠檢索去碰出這種情境既不可控也不可重現，因此把 request 端拉出來，
///   直接指定片段組合與順序。
///
/// 這個端點走的是與 /api/chat 完全相同的 PromptBuilder → LLMService 路徑，
/// 只繞過 embedding、向量檢索、去重與依快取狀態重排——因為那四件事正是
/// 我們要排除的變因。
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly IQdrantService _qdrant;
    private readonly IPromptBuilder _promptBuilder;
    private readonly ILLMService _llm;
    private readonly ILogger<DiagnosticsController> _logger;

    private const string Collection = "legal_documents";

    public DiagnosticsController(
        IQdrantService qdrant,
        IPromptBuilder promptBuilder,
        ILLMService llm,
        ILogger<DiagnosticsController> logger)
    {
        _qdrant = qdrant;
        _promptBuilder = promptBuilder;
        _llm = llm;
        _logger = logger;
    }

    /// <summary>
    /// 列出所有片段，供挑選。
    /// contains 可篩選標籤或章節，例如 ?contains=重整
    /// </summary>
    [HttpGet("chunks")]
    public async Task<ActionResult<object>> ListChunks([FromQuery] string? contains = null)
    {
        var all = await _qdrant.ScrollAllAsync(Collection);

        var rows = all
            .Where(c => string.IsNullOrEmpty(contains)
                        || (c.ArticleNumber ?? "").Contains(contains)
                        || (c.Chapter ?? "").Contains(contains)
                        || (c.Title ?? "").Contains(contains))
            .OrderBy(c => c.Title)
            .ThenBy(c => FirstArticleNumber(c.ArticleNumber))
            .Select(c => new
            {
                title = c.Title,
                articleNumber = c.ArticleNumber,
                articles = ParseArticleNumbers(c.ArticleNumber),
                chapter = c.Chapter,
                chars = c.Content.Length,
            })
            .ToList();

        return Ok(new { total = all.Count, matched = rows.Count, chunks = rows });
    }

    /// <summary>
    /// 以指定的片段與順序送出一次生成。
    /// </summary>
    [HttpPost("probe")]
    public async Task<ActionResult<object>> Probe([FromBody] ProbeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest(new { error = "question 不可為空" });
        }

        if (request.Articles.Count == 0)
        {
            return BadRequest(new { error = "articles 不可為空" });
        }

        var all = await _qdrant.ScrollAllAsync(Collection);

        // 依 request 給的順序挑片段。同一個片段被指到兩次就只取第一次，
        // 因為重複的片段對快取沒有意義，而且會讓 token 數難以對照。
        var chosen = new List<RetrievedChunk>();
        var usedLabels = new HashSet<string>();
        var notFound = new List<string>();

        foreach (var wanted in request.Articles)
        {
            var key = wanted.Trim();
            var hit = all.FirstOrDefault(c => ParseArticleNumbers(c.ArticleNumber).Contains(key));

            if (hit is null)
            {
                notFound.Add(key);
                continue;
            }

            if (!usedLabels.Add(hit.ArticleNumber ?? hit.Id))
            {
                _logger.LogInformation("條號 {Key} 落在已選過的片段「{Label}」，略過", key, hit.ArticleNumber);
                continue;
            }

            chosen.Add(new RetrievedChunk
            {
                ArticleNumber = hit.ArticleNumber ?? "",
                Content = hit.Content
            });
        }

        if (chosen.Count == 0)
        {
            return NotFound(new { error = "指定的條號都找不到對應片段", notFound });
        }

        // 不去重、不重排——順序完全照 request 給的
        var prompt = await _promptBuilder.BuildAsync(chosen, request.Question);
        var result = await _llm.GenerateFromTokensAsync(prompt.Tokens);

        // 各片段的 token 數，用來預測理論命中量。
        // 例如「只有第一段命中」時，cached 應約等於 prefixTokens + 第一段長度。
        var segments = new List<object>();
        for (var i = 0; i < chosen.Count; i++)
        {
            segments.Add(new
            {
                position = i + 1,
                label = chosen[i].Label,
                offset = prompt.ChunkOffsets[i],
                tokens = prompt.ChunkTokenCounts[i]
            });
        }

        return Ok(new
        {
            question = request.Question,
            order = chosen.Select(c => c.Label).ToList(),
            notFound,
            promptTokens = result.PromptTokens,
            cachedTokens = result.CachedTokens,
            cacheHitRate = result.CacheHitRate,
            ttftMilliseconds = result.TtftMilliseconds,
            totalMilliseconds = result.TotalMilliseconds,
            builtTokens = prompt.Tokens.Length,
            prefixTokens = prompt.PrefixTokens,
            segments,
            reply = result.Content
        });
    }

    private static List<string> ParseArticleNumbers(string? label)
    {
        // 「第 291、292、293 條」→ ["291","292","293"]
        if (string.IsNullOrEmpty(label)) return new List<string>();

        var inner = Regex.Replace(label, @"^第\s*|\s*條\s*$", "");
        return inner.Split('、', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToList();
    }

    private static int FirstArticleNumber(string? label)
    {
        var nums = ParseArticleNumbers(label);
        if (nums.Count == 0) return int.MaxValue;
        var head = Regex.Match(nums[0], @"^\d+");
        return head.Success ? int.Parse(head.Value) : int.MaxValue;
    }
}
