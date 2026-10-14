using System.Text;
using System.Text.Json;
using ChatBot.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBot.Api.Tests.Controllers;

/// <summary>
/// 可由測試決定 HasStarted 的回應 feature。
/// 預設的 HttpResponseFeature 永遠回 false，沒辦法模擬「SSE 已經送出第一段」。
/// </summary>
internal sealed class FakeResponseFeature : HttpResponseFeature
{
    public bool Started { get; set; }
    public override bool HasStarted => Started;
}

public class ChatControllerTests
{
    private readonly Mock<IChatBotService> _service = new();
    private readonly Mock<ILMCacheService> _cache = new();
    private readonly FakeResponseFeature _responseFeature = new();

    private static ChatResponse Reply(
        string raw = "答案",
        long ttft = 0, long total = 0, int tokens = 0, double tps = 0, bool fromCache = false)
        => new()
        {
            Reply = "📜 " + raw,
            RawReply = raw,
            TtftMilliseconds = ttft,
            TotalMilliseconds = total,
            TokenCount = tokens,
            TokensPerSecond = tps,
            FromCache = fromCache,
        };

    private ChatController Create(bool withCache = false)
    {
        var controller = withCache
            ? new ChatController(_service.Object, NullLogger<ChatController>.Instance, _cache.Object)
            : new ChatController(_service.Object, NullLogger<ChatController>.Instance);

        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(_responseFeature);
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection().BuildServiceProvider();
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private void ServiceReturns(ChatResponse response)
        => _service
            .Setup(s => s.GetReplyAsync(
                It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>()))
            .ReturnsAsync(response);

    private void ServiceThrows(Exception exception)
        => _service
            .Setup(s => s.GetReplyAsync(
                It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>()))
            .ThrowsAsync(exception);

    private static string BodyOf(ChatController controller)
    {
        var body = controller.HttpContext.Response.Body;
        body.Position = 0;
        return new StreamReader(body, Encoding.UTF8).ReadToEnd();
    }

    private static List<(string Event, JsonElement Data)> ParseSse(string body)
        => body
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block =>
            {
                var lines = block.Split('\n');
                var name = lines[0]["event: ".Length..];
                var data = JsonDocument.Parse(lines[1]["data: ".Length..]).RootElement.Clone();
                return (name, data);
            })
            .ToList();

    // ════════════════════════════════════════════════════════════
    //  POST /api/chat
    // ════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Chat_問題為空回_400_且不呼叫服務(string message)
    {
        var result = await Create().Chat(new ChatRequest { Message = message });

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Message cannot be empty", TestFactory.ToJson(bad.Value).GetProperty("error").GetString());
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Chat_成功時原樣回傳服務的結果()
    {
        var response = Reply("民法第 12 條");
        ServiceReturns(response);

        var result = await Create().Chat(new ChatRequest { Message = "成年幾歲" });

        Assert.Same(response, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Chat_快取模式不可用回_503_並帶出模式名稱()
    {
        ServiceThrows(new CacheModeUnavailableException(CacheModes.LmCache, "lmcache 尚未部署"));

        var result = await Create().Chat(new ChatRequest { Message = "Q", CacheMode = "lmcache" });

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, obj.StatusCode);
        var json = TestFactory.ToJson(obj.Value);
        Assert.Equal("lmcache 尚未部署", json.GetProperty("error").GetString());
        Assert.Equal(CacheModes.LmCache, json.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Chat_其他例外回_500_且訊息不洩漏內部細節()
    {
        ServiceThrows(new InvalidOperationException("connection string: Host=secret-db"));

        var result = await Create().Chat(new ChatRequest { Message = "Q" });

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, obj.StatusCode);
        var error = TestFactory.ToJson(obj.Value).GetProperty("error").GetString();
        Assert.Equal("An error occurred while processing your request", error);
        Assert.DoesNotContain("secret-db", error);
    }

    // ════════════════════════════════════════════════════════════
    //  GET /api/chat/health
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void HealthCheck_回_Healthy_且不碰任何相依()
    {
        var result = Create().HealthCheck();

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Healthy", json.GetProperty("status").GetString());
        Assert.Equal("ChatBot API", json.GetProperty("service").GetString());
        Assert.Equal("1.0.0", json.GetProperty("version").GetString());
        _service.VerifyNoOtherCalls();
    }

    // ════════════════════════════════════════════════════════════
    //  POST /api/chat/benchmark
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Benchmark_沒有問題回_400()
    {
        var controller = Create();

        Assert.IsType<BadRequestObjectResult>(
            (await controller.RunBenchmark(new BenchmarkRequest { Questions = new List<string>() })).Result);
        Assert.IsType<BadRequestObjectResult>(
            (await controller.RunBenchmark(new BenchmarkRequest { Questions = null! })).Result);
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Benchmark_統計值依每次回應計算()
    {
        _service
            .SetupSequence(s => s.GetReplyAsync(
                It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>()))
            .ReturnsAsync(Reply(ttft: 10, total: 100, tokens: 10, tps: 5))
            .ReturnsAsync(Reply(ttft: 30, total: 300, tokens: 20, tps: 15, fromCache: true));

        var result = await Create().RunBenchmark(
            new BenchmarkRequest { Questions = new List<string> { "Q" }, Iterations = 2 });

        var response = Assert.IsType<BenchmarkResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var q = Assert.Single(response.Results);

        Assert.Equal("Q", q.Question);
        Assert.Equal(2, q.Iterations.Count);
        Assert.Equal(new[] { 1, 2 }, q.Iterations.Select(i => i.Iteration));
        Assert.Equal(20, q.AverageTtftMs);
        Assert.Equal(10, q.MinTtftMs);
        Assert.Equal(30, q.MaxTtftMs);
        Assert.Equal(10, q.StdDevTtftMs, precision: 6);   // 母體標準差：值 10 與 30、平均 20
        Assert.Equal(200, q.AverageTotalMs);
        Assert.Equal(15, q.AverageTokens);
        Assert.Equal(10, q.AverageThroughput);
        Assert.False(q.Iterations[0].FromCache);
        Assert.True(q.Iterations[1].FromCache);

        Assert.Equal(20, response.Summary.AverageTtftMs);
        Assert.Equal(200, response.Summary.AverageTotalMs);
        Assert.Equal(10, response.Summary.AverageThroughput);
        Assert.Equal(50, response.Summary.CacheHitRate);
    }

    [Fact]
    public async Task Benchmark_每個問題各問_Iterations_次_用的是各自的問題()
    {
        ServiceReturns(Reply(ttft: 1, total: 1, tokens: 1, tps: 1));

        var result = await Create().RunBenchmark(
            new BenchmarkRequest { Questions = new List<string> { "Q1", "Q2" }, Iterations = 3 });

        var response = Assert.IsType<BenchmarkResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(new[] { "Q1", "Q2" }, response.Results.Select(r => r.Question));

        foreach (var question in new[] { "Q1", "Q2" })
        {
            _service.Verify(s => s.GetReplyAsync(
                It.Is<ChatRequest>(r => r.Message == question),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<string, Task>?>()), Times.Exactly(3));
        }
    }

    [Fact]
    public async Task Benchmark_Iterations_為零時不呼叫服務_統計為零()
    {
        var result = await Create().RunBenchmark(
            new BenchmarkRequest { Questions = new List<string> { "Q" }, Iterations = 0 });

        var response = Assert.IsType<BenchmarkResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(Assert.Single(response.Results).Iterations);
        Assert.Equal(0, response.Summary.CacheHitRate);
        Assert.Equal(0, response.Summary.AverageTtftMs);
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Benchmark_回答快取啟用時先清空並在設定欄位註明()
    {
        _cache.SetupGet(c => c.IsEnabled).Returns(true);
        ServiceReturns(Reply());

        var result = await Create(withCache: true).RunBenchmark(
            new BenchmarkRequest { Questions = new List<string> { "Q" }, Iterations = 1 });

        var response = Assert.IsType<BenchmarkResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        _cache.Verify(c => c.Clear(), Times.Once);
        Assert.Equal("RAG + vLLM(LMCache/CacheBlend) + C# 回答快取", response.Setting);
    }

    [Fact]
    public async Task Benchmark_回答快取未啟用時不清空_設定欄位不含快取字樣()
    {
        _cache.SetupGet(c => c.IsEnabled).Returns(false);
        ServiceReturns(Reply());

        var result = await Create(withCache: true).RunBenchmark(
            new BenchmarkRequest { Questions = new List<string> { "Q" }, Iterations = 1 });

        var response = Assert.IsType<BenchmarkResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        _cache.Verify(c => c.Clear(), Times.Never);
        Assert.Equal("RAG + vLLM(LMCache/CacheBlend)", response.Setting);
    }

    [Fact]
    public async Task Benchmark_清空快取失敗不影響測試進行()
    {
        _cache.SetupGet(c => c.IsEnabled).Returns(true);
        _cache.Setup(c => c.Clear()).Throws(new InvalidOperationException("boom"));
        ServiceReturns(Reply());

        var result = await Create(withCache: true).RunBenchmark(
            new BenchmarkRequest { Questions = new List<string> { "Q" }, Iterations = 1 });

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Benchmark_服務丟例外時回_500()
    {
        ServiceThrows(new InvalidOperationException("boom"));

        var result = await Create().RunBenchmark(
            new BenchmarkRequest { Questions = new List<string> { "Q" }, Iterations = 1 });

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, obj.StatusCode);
        Assert.Equal("An error occurred while running benchmark",
            TestFactory.ToJson(obj.Value).GetProperty("error").GetString());
    }

    // ════════════════════════════════════════════════════════════
    //  POST /api/chat/stream（SSE）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Stream_問題為空回_400_不開始串流()
    {
        var controller = Create();

        await controller.ChatStream(new ChatRequest { Message = " " }, CancellationToken.None);

        Assert.Equal(400, controller.Response.StatusCode);
        Assert.Contains("Message cannot be empty", BodyOf(controller));
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Stream_依序送出_delta_再送_done()
    {
        _service
            .Setup(s => s.GetReplyAsync(
                It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>()))
            .Returns<ChatRequest, CancellationToken, Func<string, Task>?>(async (_, _, onDelta) =>
            {
                await onDelta!("Hel");
                await onDelta("lo");
                return Reply("Hello", ttft: 12, total: 34);
            });
        var controller = Create();

        await controller.ChatStream(new ChatRequest { Message = "Q" }, CancellationToken.None);

        var events = ParseSse(BodyOf(controller));
        Assert.Equal(new[] { "delta", "delta", "done" }, events.Select(e => e.Event));
        Assert.Equal("Hel", events[0].Data.GetProperty("text").GetString());
        Assert.Equal("lo", events[1].Data.GetProperty("text").GetString());

        var done = events[2].Data;
        Assert.Equal("Hello", done.GetProperty("rawReply").GetString());   // Web 預設：camelCase
        Assert.Equal(12, done.GetProperty("ttftMilliseconds").GetInt64());
        Assert.Equal(34, done.GetProperty("totalMilliseconds").GetInt64());
    }

    [Fact]
    public async Task Stream_回應標頭符合_SSE_規格且關閉緩衝()
    {
        ServiceReturns(Reply());
        var controller = Create();

        await controller.ChatStream(new ChatRequest { Message = "Q" }, CancellationToken.None);

        var response = controller.Response;
        Assert.Equal(200, response.StatusCode);
        Assert.StartsWith("text/event-stream", response.ContentType);
        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
        Assert.Equal("no", response.Headers["X-Accel-Buffering"].ToString());
    }

    [Fact]
    public async Task Stream_把請求與取消權杖原樣交給服務()
    {
        ServiceReturns(Reply());
        var controller = Create();
        var request = new ChatRequest { Message = "Q", CacheMode = "none" };
        using var cts = new CancellationTokenSource();

        await controller.ChatStream(request, cts.Token);

        _service.Verify(s => s.GetReplyAsync(
            request, cts.Token, It.IsNotNull<Func<string, Task>>()), Times.Once);
    }

    [Fact]
    public async Task Stream_第一段之前模式不可用_仍用_503_表達()
    {
        ServiceThrows(new CacheModeUnavailableException(CacheModes.None, "none 尚未部署"));
        var controller = Create();

        await controller.ChatStream(new ChatRequest { Message = "Q" }, CancellationToken.None);

        Assert.Equal(503, controller.Response.StatusCode);
        var json = JsonDocument.Parse(BodyOf(controller)).RootElement;
        Assert.Equal("none 尚未部署", json.GetProperty("error").GetString());
        Assert.Equal(CacheModes.None, json.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Stream_第一段之前發生其他錯誤_回_500_且不洩漏細節()
    {
        ServiceThrows(new InvalidOperationException("Host=secret-db"));
        var controller = Create();

        await controller.ChatStream(new ChatRequest { Message = "Q" }, CancellationToken.None);

        Assert.Equal(500, controller.Response.StatusCode);
        var body = BodyOf(controller);
        Assert.Contains("An error occurred while processing your request", body);
        Assert.DoesNotContain("secret-db", body);
    }

    [Fact]
    public async Task Stream_已送出內容後失敗_改以_error_事件通知_狀態碼維持_200()
    {
        _service
            .Setup(s => s.GetReplyAsync(
                It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>()))
            .Returns<ChatRequest, CancellationToken, Func<string, Task>?>(async (_, _, onDelta) =>
            {
                await onDelta!("partial");
                _responseFeature.Started = true;     // 第一段已經送出，不能再改狀態碼
                throw new InvalidOperationException("Host=secret-db");
            });
        var controller = Create();

        await controller.ChatStream(new ChatRequest { Message = "Q" }, CancellationToken.None);

        Assert.Equal(200, controller.Response.StatusCode);
        var events = ParseSse(BodyOf(controller));
        Assert.Equal(new[] { "delta", "error" }, events.Select(e => e.Event));
        var message = events[1].Data.GetProperty("error").GetString();
        Assert.Equal("生成中斷，已顯示的內容可能不完整", message);
        Assert.DoesNotContain("secret-db", message);
    }

    [Fact]
    public async Task Stream_已送出內容後模式不可用_error_事件帶原訊息()
    {
        _responseFeature.Started = true;
        ServiceThrows(new CacheModeUnavailableException(CacheModes.LmCache, "引擎中途離線"));
        var controller = Create();

        await controller.ChatStream(new ChatRequest { Message = "Q" }, CancellationToken.None);

        var events = ParseSse(BodyOf(controller));
        var error = Assert.Single(events);
        Assert.Equal("error", error.Event);
        Assert.Equal("引擎中途離線", error.Data.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Stream_瀏覽器斷線_安靜結束_不寫任何東西()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ServiceThrows(new OperationCanceledException(cts.Token));
        var controller = Create();

        var exception = await Record.ExceptionAsync(
            () => controller.ChatStream(new ChatRequest { Message = "Q" }, cts.Token));

        Assert.Null(exception);
        Assert.Equal(0, controller.Response.Body.Length);
    }

    [Fact]
    public async Task Stream_逾時的_TaskCanceledException_不是斷線_要走失敗流程()
    {
        // HttpClient 逾時丟 TaskCanceledException，但呼叫端的權杖並未取消
        ServiceThrows(new TaskCanceledException("timeout"));
        var controller = Create();

        await controller.ChatStream(new ChatRequest { Message = "Q" }, CancellationToken.None);

        Assert.Equal(500, controller.Response.StatusCode);
    }

    [Fact]
    public async Task Stream_最後一筆寫入時斷線不拋例外()
    {
        using var cts = new CancellationTokenSource();
        _service
            .Setup(s => s.GetReplyAsync(
                It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>()))
            .Returns<ChatRequest, CancellationToken, Func<string, Task>?>((_, _, _) =>
            {
                cts.Cancel();     // 生成完成的瞬間瀏覽器剛好離開
                return Task.FromResult(Reply());
            });
        var controller = Create();

        var exception = await Record.ExceptionAsync(
            () => controller.ChatStream(new ChatRequest { Message = "Q" }, cts.Token));

        Assert.Null(exception);
    }
}
