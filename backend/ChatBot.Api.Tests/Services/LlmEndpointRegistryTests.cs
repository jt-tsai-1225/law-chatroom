using System.Net;

namespace ChatBot.Api.Tests.Services;

public class LlmEndpointRegistryTests
{
    private static LlmEndpointRegistry Create(
        StubHttpMessageHandler handler, Action<RAGSettings>? configure = null)
        => new(
            TestFactory.Options(configure),
            TestFactory.HttpClientFactory(handler).Object,
            NullLogger<LlmEndpointRegistry>.Instance);

    private static StubHttpMessageHandler NeverCalled()
        => new(_ => throw new InvalidOperationException("這個測試不該打網路"));

    // ── ResolveBaseUrl ──────────────────────────────────────────

    [Fact]
    public void 沒設定對應表時_CacheBlend_退回單一端點設定_其餘為_null()
    {
        var registry = Create(NeverCalled(), s =>
        {
            s.LlmBaseUrl = "http://legacy:8000";
            s.LlmEndpoints = null;
        });

        Assert.Equal("http://legacy:8000", registry.ResolveBaseUrl(CacheModes.CacheBlend));
        Assert.Null(registry.ResolveBaseUrl(CacheModes.LmCache));
        Assert.Null(registry.ResolveBaseUrl(CacheModes.None));
    }

    [Fact]
    public void 對應表是空的時候與沒設定相同()
    {
        var registry = Create(NeverCalled(), s =>
        {
            s.LlmBaseUrl = "http://legacy:8000";
            s.LlmEndpoints = new Dictionary<string, string>();
        });

        Assert.Equal("http://legacy:8000", registry.ResolveBaseUrl(CacheModes.CacheBlend));
        Assert.Null(registry.ResolveBaseUrl(CacheModes.LmCache));
    }

    [Fact]
    public void 對應表有設定的模式回傳該端點並去掉結尾斜線()
    {
        var registry = Create(NeverCalled(), s => s.LlmEndpoints = new Dictionary<string, string>
        {
            [CacheModes.CacheBlend] = "http://blend:8000/",
            [CacheModes.LmCache] = "http://lm:8001",
        });

        Assert.Equal("http://blend:8000", registry.ResolveBaseUrl(CacheModes.CacheBlend));
        Assert.Equal("http://lm:8001", registry.ResolveBaseUrl(CacheModes.LmCache));
    }

    [Fact]
    public void 沒列在對應表或值是空白的模式回_null_而不是退回別的引擎()
    {
        var registry = Create(NeverCalled(), s => s.LlmEndpoints = new Dictionary<string, string>
        {
            [CacheModes.CacheBlend] = "http://blend:8000",
            [CacheModes.None] = "   ",
        });

        Assert.Null(registry.ResolveBaseUrl(CacheModes.LmCache));
        Assert.Null(registry.ResolveBaseUrl(CacheModes.None));
    }

    [Fact]
    public void 對應表沒有_CacheBlend_時退回舊的單一端點()
    {
        var registry = Create(NeverCalled(), s =>
        {
            s.LlmBaseUrl = "http://legacy:8000";
            s.LlmEndpoints = new Dictionary<string, string> { [CacheModes.LmCache] = "http://lm:8001" };
        });

        Assert.Equal("http://legacy:8000", registry.ResolveBaseUrl(CacheModes.CacheBlend));
    }

    // ── GetStatusAsync ──────────────────────────────────────────

    private static void ThreeModes(RAGSettings s)
        => s.LlmEndpoints = new Dictionary<string, string>
        {
            [CacheModes.CacheBlend] = "http://blend",
            [CacheModes.LmCache] = "http://lm",
            [CacheModes.None] = "",
        };

    [Fact]
    public async Task 狀態依探測結果回報_未設定的模式不會被探測()
    {
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.Host == "blend"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var registry = Create(handler, ThreeModes);

        var status = await registry.GetStatusAsync();

        Assert.Equal(new[] { CacheModes.CacheBlend, CacheModes.LmCache, CacheModes.None },
            status.Select(s => s.Mode));

        var blend = status[0];
        Assert.True(blend.Configured);
        Assert.True(blend.Reachable);
        Assert.Equal("http://blend", blend.BaseUrl);
        Assert.Null(blend.Note);
        Assert.Equal(CacheModes.DisplayName(CacheModes.CacheBlend), blend.DisplayName);

        var lm = status[1];
        Assert.True(lm.Configured);
        Assert.False(lm.Reachable);
        Assert.NotNull(lm.Note);

        var none = status[2];
        Assert.False(none.Configured);
        Assert.False(none.Reachable);
        Assert.Equal("未設定端點", none.Note);
        Assert.Null(none.BaseUrl);

        // 只探測有設定的兩個，而且打的是 /v1/models
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.EndsWith("/v1/models", r.RequestUri!.AbsoluteUri));
    }

    [Fact]
    public async Task 探測丟出網路例外時視為不可用而不是讓整個查詢失敗()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var registry = Create(handler, ThreeModes);

        var status = await registry.GetStatusAsync();

        Assert.False(status[0].Reachable);
        Assert.False(status[1].Reachable);
    }

    [Fact]
    public async Task 探測結果在有效期內被快取_第二次查詢不再打網路()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var registry = Create(handler, ThreeModes);

        await registry.GetStatusAsync();
        var firstCount = handler.Requests.Count;
        await registry.GetStatusAsync();

        Assert.Equal(2, firstCount);
        Assert.Equal(firstCount, handler.Requests.Count);
    }

    [Fact]
    public async Task 呼叫端取消時例外照常往外傳()
    {
        var registry = Create(NeverCalled(), ThreeModes);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registry.GetStatusAsync(cts.Token));
    }
}
