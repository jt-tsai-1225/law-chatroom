using ChatBot.Api.Models;
using ChatBot.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IChatBotService _chatBotService;
    private readonly ILogger<ChatController> _logger;
    private readonly ILMCacheService? _cacheService;

    public ChatController(
        IChatBotService chatBotService,
        ILogger<ChatController> logger,
        ILMCacheService? cacheService = null)
    {
        _chatBotService = chatBotService;
        _logger = logger;
        _cacheService = cacheService;
    }

    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Chat([FromBody] ChatRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new { error = "Message cannot be empty" });
            }

            var response = await _chatBotService.GetReplyAsync(request);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing chat request");
            return StatusCode(500, new { error = "An error occurred while processing your request" });
        }
    }

    [HttpGet("health")]
    public ActionResult<object> HealthCheck()
    {
        return Ok(new
        {
            status = "Healthy",
            timestamp = DateTime.UtcNow,
            service = "ChatBot API",
            version = "1.0.0"
        });
    }

    /// <summary>
    /// 執行基準測試
    /// </summary>
    [HttpPost("benchmark")]
    public async Task<ActionResult<BenchmarkResponse>> RunBenchmark([FromBody] BenchmarkRequest request)
    {
        try
        {
            if (request.Questions == null || request.Questions.Count == 0)
            {
                return BadRequest(new { error = "Questions cannot be empty" });
            }

            var results = new List<BenchmarkResult>();
            var allTtftValues = new List<double>();
            var allTotalValues = new List<double>();
            var allThroughputValues = new List<double>();
            var totalCacheHits = 0L;
            var totalIterations = 0L;

            // 在測試開始前清空快取
            try
            {
                if (_cacheService != null && _cacheService.IsEnabled)
                {
                    _cacheService.Clear();
                    _logger.LogInformation("Benchmark 開始前已清空快取");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Benchmark 清空快取失敗");
            }

            foreach (var question in request.Questions)
            {
                var questionResult = new BenchmarkResult { Question = question };
                var questionTtftValues = new List<double>();
                var questionTotalValues = new List<double>();
                var questionThroughputValues = new List<double>();
                
                for (int i = 1; i <= request.Iterations; i++)
                {
                    var chatRequest = new ChatRequest { Message = question };
                    var response = await _chatBotService.GetReplyAsync(chatRequest);
                    
                    var iterationResult = new IterationResult
                    {
                        Iteration = i,
                        TtftMs = response.TtftMilliseconds,
                        TotalMs = response.TotalMilliseconds,
                        TokenCount = response.TokenCount,
                        Throughput = response.TokensPerSecond,
                        FromCache = response.FromCache
                    };
                    
                    questionResult.Iterations.Add(iterationResult);
                    questionTtftValues.Add(response.TtftMilliseconds);
                    questionTotalValues.Add(response.TotalMilliseconds);
                    questionThroughputValues.Add(response.TokensPerSecond);
                    
                    if (response.FromCache)
                    {
                        totalCacheHits++;
                    }
                    totalIterations++;
                }

                // 計算統計數據
                if (questionTtftValues.Count > 0)
                {
                    questionResult.AverageTtftMs = questionTtftValues.Average();
                    questionResult.MinTtftMs = questionTtftValues.Min();
                    questionResult.MaxTtftMs = questionTtftValues.Max();
                    
                    var mean = questionTtftValues.Average();
                    var variance = questionTtftValues.Sum(v => Math.Pow(v - mean, 2)) / questionTtftValues.Count;
                    questionResult.StdDevTtftMs = Math.Sqrt(variance);
                    
                    questionResult.AverageTotalMs = questionTotalValues.Average();
                    questionResult.AverageTokens = questionThroughputValues.Any()
                        ? questionResult.Iterations.Average(r => r.TokenCount)
                        : 0;
                    questionResult.AverageThroughput = questionThroughputValues.Any()
                        ? questionThroughputValues.Average()
                        : 0;
                    
                    allTtftValues.AddRange(questionTtftValues);
                    allTotalValues.AddRange(questionTotalValues);
                    allThroughputValues.AddRange(questionThroughputValues);
                }

                results.Add(questionResult);
            }

            // 計算總體統計
            var summary = new SummaryStats
            {
                AverageTtftMs = allTtftValues.Any() ? allTtftValues.Average() : 0,
                AverageTotalMs = allTotalValues.Any() ? allTotalValues.Average() : 0,
                AverageThroughput = allThroughputValues.Any() ? allThroughputValues.Average() : 0,
                CacheHitRate = totalIterations > 0 ? (double)totalCacheHits / totalIterations * 100 : 0
            };

            // 獲取當前設定
            // CacheBlend 是否生效不看 C# 這層，而看每次回答的 cached_tokens；
            // 這裡只記錄 C# 回答快取的開關狀態。
            var setting = "RAG + vLLM(LMCache/CacheBlend)"
                          + (_cacheService is { IsEnabled: true } ? " + C# 回答快取" : "");

            var benchmarkResponse = new BenchmarkResponse
            {
                Setting = setting,
                Results = results,
                Summary = summary
            };

            return Ok(benchmarkResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running benchmark");
            return StatusCode(500, new { error = "An error occurred while running benchmark" });
        }
    }
}
