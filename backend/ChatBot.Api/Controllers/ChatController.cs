using ChatBot.Api.Models;
using ChatBot.Api.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ChatBot.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    // SSE 的 delta/done 事件內容用 Web 預設序列化（camelCase），
    // 與既有 /api/chat 回應的欄位命名一致，前端可以共用同一組欄位名。
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

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
        catch (CacheModeUnavailableException ex)
        {
            // 這不是伺服器錯誤，是使用者選了一個目前沒有部署的模式。
            // 回 503 並說明原因，讓前端能顯示「這個模式尚未啟動」而不是
            // 一句無從查起的「發生錯誤」。
            _logger.LogWarning("快取模式 {Mode} 不可用：{Message}", ex.Mode, ex.Message);
            return StatusCode(503, new { error = ex.Message, mode = ex.Mode });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing chat request");
            return StatusCode(500, new { error = "An error occurred while processing your request" });
        }
    }

    /// <summary>
    /// 聊天的串流版本。以 SSE（text/event-stream）把生成中的文字逐段送給前端，
    /// 讓使用者不必等整段答案生成完才看到第一個字。
    ///
    /// 事件格式：
    ///   event: delta  data: {"text":"…"}   一段生成中的文字（可多筆）
    ///   event: done   data: {…ChatResponse} 生成結束，欄位與 /api/chat 相同
    ///   event: error  data: {"error":"…"}  生成中斷（此時已無法改 HTTP 狀態碼）
    ///
    /// 失敗若發生在第一個 delta 之前（選了未啟動的模式、連不上引擎、
    /// 查無條文前的檢索錯誤），仍以 HTTP 狀態碼回報（503/500/400），
    /// 與 /api/chat 相同語意，前端能用同一套錯誤處理。
    /// </summary>
    [HttpPost("stream")]
    [Produces("text/event-stream")]
    public async Task ChatStream([FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { error = "Message cannot be empty" }, cancellationToken);
            return;
        }

        // SSE 標頭必須在第一次寫入前定案。
        // X-Accel-Buffering 是給 nginx 看的：有這個標頭，nginx 會對這一個
        // 回應關閉 proxy_buffering，逐段轉發而不攢批（nginx.conf 的
        // /api/chat/stream location 也設了 proxy_buffering off，兩邊是雙保險）。
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Append("X-Accel-Buffering", "no");

        async Task WriteEventAsync(string eventName, string payload)
        {
            await Response.WriteAsync($"event: {eventName}\ndata: {payload}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }

        // 注意 TaskCanceledException：HttpClient 逾時也會丟它（它是
        // OperationCanceledException 的子類），但那與瀏覽器斷線不同——
        // 瀏覽器斷線以「請求已取消」過濾後安靜結束，逾時要走下面的失敗流程。
        ChatResponse? final = null;
        Exception? failure = null;
        try
        {
            final = await _chatBotService.GetReplyAsync(request, cancellationToken, async delta =>
            {
                await WriteEventAsync("delta", JsonSerializer.Serialize(new { text = delta }, JsonOpts));
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 瀏覽器切換聊天室或關閉頁面會中斷連線，不是伺服器錯誤，安靜結束。
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is not null)
        {
            if (!Response.HasStarted)
            {
                // 一個字都還沒送出去，還可以用 HTTP 狀態碼表達失敗，
                // 前端沿用 /api/chat 的錯誤處理（503 = 模式未啟動）。
                if (failure is CacheModeUnavailableException unavailable)
                {
                    _logger.LogWarning("快取模式 {Mode} 不可用：{Message}", unavailable.Mode, failure.Message);
                    Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await Response.WriteAsJsonAsync(
                        new { error = failure.Message, mode = unavailable.Mode }, cancellationToken);
                }
                else
                {
                    _logger.LogError(failure, "Error processing streaming chat request");
                    Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await Response.WriteAsJsonAsync(
                        new { error = "An error occurred while processing your request" }, cancellationToken);
                }

                return;
            }

            // 內容已送出一部分，SSE 開始後不能改狀態碼，改以 error 事件告知。
            // 寫入本身失敗（瀏覽器已斷線）就無能為力，安靜結束即可。
            try
            {
                var message = failure is CacheModeUnavailableException modeEx
                    ? modeEx.Message
                    : "生成中斷，已顯示的內容可能不完整";
                await WriteEventAsync("error", JsonSerializer.Serialize(new { error = message }, JsonOpts));
            }
            catch (OperationCanceledException)
            {
            }

            return;
        }

        // done 事件帶整包 ChatResponse（含 TTFT、命中率、persisted 等量測欄位），
        // 與 /api/chat 的回應同構，前端在這一刻把統計區一次補齊。
        // 最後一筆寫入也可能碰上瀏覽器斷線，同樣安靜結束。
        try
        {
            await WriteEventAsync("done", JsonSerializer.Serialize(final, JsonOpts));
        }
        catch (OperationCanceledException)
        {
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
