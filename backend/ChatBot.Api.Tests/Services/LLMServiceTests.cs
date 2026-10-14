using System.Net;
using System.Text.Json;

namespace ChatBot.Api.Tests.Services;

public class LLMServiceTests
{
    private readonly Mock<ILlmEndpointRegistry> _endpoints = new();
    private readonly Mock<ILMCacheService> _cache = new();

    public LLMServiceTests()
    {
        _endpoints.Setup(e => e.ResolveBaseUrl(It.IsAny<string>())).Returns("http://llm:8000/");
    }

    // ── 測試用的小工具 ─────────────────────────────────────────

    private LLMService Create(
        StubHttpMessageHandler handler, Action<RAGSettings>? configure = null, bool withCache = false)
    {
        var factory = TestFactory.HttpClientFactory(handler);
        var options = TestFactory.Options(s =>
        {
            s.LlmModel = "test-model";
            s.LlmApiKey = "";
            configure?.Invoke(s);
        });

        return withCache
            ? new LLMService(factory.Object, options, _endpoints.Object,
                NullLogger<LLMService>.Instance, _cache.Object)
            : new LLMService(factory.Object, options, _endpoints.Object,
                NullLogger<LLMService>.Instance);
    }

    private static string Delta(string text)
        => "{\"choices\":[{\"text\":" + JsonSerializer.Serialize(text) + "}]}";

    private static string Usage(int prompt, int cached)
        => "{\"choices\":[],\"usage\":{\"prompt_tokens\":" + prompt
           + ",\"prompt_tokens_details\":{\"cached_tokens\":" + cached + "}}}";

    /// <summary>把事件包成 SSE 本文，結尾加上 [DONE]。</summary>
    private static string Body(params string[] events)
        => string.Concat(events.Select(e => "data: " + e + "\n\n")) + "data: [DONE]\n\n";

    private static StubHttpMessageHandler Streaming(string body)
        => new(_ => StubHttpMessageHandler.Sse(body));

    private static StubHttpMessageHandler NeverCalled()
        => new(_ => throw new InvalidOperationException("這個測試不該打網路"));

    private static readonly int[] Tokens = { 1, 2, 3 };

    // ════════════════════════════════════════════════════════════
    //  串流解析
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task 串流的文字片段合成答案_usage_決定命中率()
    {
        var handler = Streaming(Body(Delta("你"), Delta("好"), Usage(1000, 800)));

        var result = await Create(handler).GenerateFromTokensAsync(Tokens);

        Assert.Equal("你好", result.Content);
        Assert.Equal(2, result.TokenCount);
        Assert.Equal(1000, result.PromptTokens);
        Assert.Equal(800, result.CachedTokens);
        Assert.Equal(80, result.CacheHitRate);
        Assert.False(result.FromCache);
        Assert.InRange(result.TtftMilliseconds, 0, result.TotalMilliseconds);
        Assert.True(result.TokensPerSecond >= 0);
    }

    [Fact]
    public async Task 每段文字都即時回呼給呼叫端()
    {
        var handler = Streaming(Body(Delta("甲"), Delta("乙")));
        var deltas = new List<string>();

        await Create(handler).GenerateFromTokensAsync(Tokens, onTokenDelta: d =>
        {
            deltas.Add(d);
            return Task.CompletedTask;
        });

        Assert.Equal(new[] { "甲", "乙" }, deltas);
    }

    [Fact]
    public async Task 答案頭尾的空白被修掉()
    {
        var handler = Streaming(Body(Delta("  你好"), Delta(" \n")));

        var result = await Create(handler).GenerateFromTokensAsync(Tokens);

        Assert.Equal("你好", result.Content);
    }

    [Fact]
    public async Task 壞掉的事件_非資料行_空資料行都被略過_遇到_DONE_就停()
    {
        var body =
            "data: {not json\n\n" +
            "data: " + Delta("甲") + "\n\n" +
            "event: ping\n\n" +
            ": comment\n\n" +
            "data:\n\n" +
            "data: " + Delta("乙") + "\n\n" +
            "data: [DONE]\n\n" +
            "data: " + Delta("丙") + "\n\n";

        var result = await Create(Streaming(body)).GenerateFromTokensAsync(Tokens);

        Assert.Equal("甲乙", result.Content);
        Assert.Equal(2, result.TokenCount);
    }

    [Fact]
    public async Task 結構不完整的事件不影響後續文字()
    {
        var handler = Streaming(Body(
            "{\"id\":\"x\"}",                                   // 沒有 choices
            "{\"choices\":{}}",                                 // choices 不是陣列
            "{\"choices\":[{\"index\":0}]}",                    // 沒有 text
            "{\"choices\":[{\"text\":\"\"}]}",                  // 空字串
            "{\"choices\":[{\"text\":null}]}",                  // null
            "{\"usage\":null,\"choices\":[{\"text\":\"A\"}]}",  // usage 不是物件
            "{\"usage\":{\"prompt_tokens\":\"x\"},\"choices\":[]}",   // 欄位型別錯
            "{\"usage\":{\"prompt_tokens\":10,\"prompt_tokens_details\":5},\"choices\":[]}"));

        var result = await Create(handler).GenerateFromTokensAsync(Tokens);

        Assert.Equal("A", result.Content);
        Assert.Equal(1, result.TokenCount);
        Assert.Equal(10, result.PromptTokens);
        Assert.Equal(0, result.CachedTokens);
    }

    [Fact]
    public async Task 回應沒有_usage_時命中率為零_不丟例外()
    {
        var handler = Streaming(Body(Delta("好")));

        var result = await Create(handler).GenerateFromTokensAsync(Tokens);

        Assert.Equal(0, result.PromptTokens);
        Assert.Equal(0, result.CacheHitRate);
    }

    [Fact]
    public async Task 完全沒有文字事件時答案為空_首字時間等於總耗時()
    {
        var handler = Streaming(Body(Usage(10, 0)));

        var result = await Create(handler).GenerateFromTokensAsync(Tokens);

        Assert.Equal("", result.Content);
        Assert.Equal(0, result.TokenCount);
        Assert.Equal(result.TotalMilliseconds, result.TtftMilliseconds);
    }

    // ════════════════════════════════════════════════════════════
    //  送出的請求
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task 請求送到_completions_端點_本文欄位正確()
    {
        var handler = Streaming(Body(Delta("好")));

        await Create(handler, s =>
        {
            s.LlmMaxTokens = 256;
            s.LlmTemperature = 0.5;
        }).GenerateFromTokensAsync(Tokens);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://llm:8000/v1/completions", request.RequestUri!.ToString());

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = body.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.Equal(Tokens, root.GetProperty("prompt").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(256, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.5, root.GetProperty("temperature").GetDouble());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task 單次指定的生成長度優先於設定值()
    {
        var handler = Streaming(Body(Delta("好")));

        await Create(handler, s => s.LlmMaxTokens = 256)
            .GenerateFromTokensAsync(Tokens, maxTokens: 1);

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(1, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task 有設定金鑰才帶_Bearer_標頭()
    {
        var withKey = Streaming(Body(Delta("好")));
        await Create(withKey, s => s.LlmApiKey = "k-123").GenerateFromTokensAsync(Tokens);

        var auth = Assert.Single(withKey.Requests).Headers.Authorization;
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("k-123", auth.Parameter);

        var withoutKey = Streaming(Body(Delta("好")));
        await Create(withoutKey).GenerateFromTokensAsync(Tokens);

        Assert.Null(Assert.Single(withoutKey.Requests).Headers.Authorization);
    }

    [Fact]
    public async Task 快取模式經正規化後才查端點()
    {
        var handler = Streaming(Body(Delta("好")));

        await Create(handler).GenerateFromTokensAsync(Tokens, cacheMode: "lm");

        _endpoints.Verify(e => e.ResolveBaseUrl(CacheModes.LmCache), Times.Once);
    }

    [Fact]
    public void 逾時秒數套用在_HttpClient_上()
    {
        var client = new HttpClient(NeverCalled());
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        _ = new LLMService(
            factory.Object,
            TestFactory.Options(s => s.LlmTimeoutSeconds = 7),
            _endpoints.Object,
            NullLogger<LLMService>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
    }

    // ════════════════════════════════════════════════════════════
    //  失敗
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task 該模式沒有端點時丟出模式不可用_且不打網路()
    {
        _endpoints.Setup(e => e.ResolveBaseUrl(CacheModes.None)).Returns((string?)null);
        var handler = NeverCalled();

        var ex = await Assert.ThrowsAsync<CacheModeUnavailableException>(
            () => Create(handler).GenerateFromTokensAsync(Tokens, cacheMode: "none"));

        Assert.Equal(CacheModes.None, ex.Mode);
        Assert.Contains(CacheModes.DisplayName(CacheModes.None), ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 連線層失敗轉成模式不可用()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("refused"));

        var ex = await Assert.ThrowsAsync<CacheModeUnavailableException>(
            () => Create(handler).GenerateFromTokensAsync(Tokens));

        Assert.Equal(CacheModes.CacheBlend, ex.Mode);
        Assert.Contains("連不上", ex.Message);
    }

    [Fact]
    public async Task 引擎回錯誤碼時丟出含狀態碼與內容的例外_不當成模式不可用()
    {
        var handler = new StubHttpMessageHandler(
            _ => StubHttpMessageHandler.Text("model overloaded", HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Create(handler).GenerateFromTokensAsync(Tokens));

        Assert.Contains("500", ex.Message);
        Assert.Contains("model overloaded", ex.Message);
    }

    // ════════════════════════════════════════════════════════════
    //  C# 層回答快取
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task 快取命中時直接回傳_不送往引擎_首字時間記為零()
    {
        _cache.SetupGet(c => c.IsEnabled).Returns(true);
        _cache.Setup(c => c.Get(It.IsAny<string>())).Returns("舊答案");
        var handler = NeverCalled();

        var result = await Create(handler, withCache: true).GenerateFromTokensAsync(Tokens);

        Assert.True(result.FromCache);
        Assert.Equal("舊答案", result.Content);
        Assert.Equal(0, result.TtftMilliseconds);
        Assert.Equal(4, result.TokenCount);     // 3 個中文字 + 1 個詞 × 1.3 取整
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 後備的_token_估算_英文以詞數乘一點三()
    {
        _cache.SetupGet(c => c.IsEnabled).Returns(true);
        _cache.Setup(c => c.Get(It.IsAny<string>())).Returns("hello world foo");

        var result = await Create(NeverCalled(), withCache: true).GenerateFromTokensAsync(Tokens);

        Assert.Equal(3, result.TokenCount);     // 3 × 1.3 = 3.9 取整
    }

    [Fact]
    public async Task 快取未命中時照常生成_並把答案存進快取()
    {
        _cache.SetupGet(c => c.IsEnabled).Returns(true);
        _cache.Setup(c => c.Get(It.IsAny<string>())).Returns((string?)null);
        var handler = Streaming(Body(Delta("你"), Delta("好")));

        var result = await Create(handler, withCache: true).GenerateFromTokensAsync(Tokens);

        var key = LMCacheService.GenerateCacheKey(
            string.Join(',', Tokens), $"{Tokens.Length}:{CacheModes.CacheBlend}");
        Assert.False(result.FromCache);
        _cache.Verify(c => c.Get(key), Times.Once);
        _cache.Verify(c => c.Set(key, "你好", It.IsAny<Dictionary<string, object>?>()), Times.Once);
    }

    [Fact]
    public async Task 快取鍵含模式_切換模式不會拿到另一個引擎的舊答案()
    {
        var keys = new List<string>();
        _cache.SetupGet(c => c.IsEnabled).Returns(true);
        _cache.Setup(c => c.Get(It.IsAny<string>()))
              .Callback<string>(k => keys.Add(k))
              .Returns((string?)null);
        var service = Create(Streaming(Body(Delta("好"))), withCache: true);

        await service.GenerateFromTokensAsync(Tokens, cacheMode: "cacheblend");
        await service.GenerateFromTokensAsync(Tokens, cacheMode: "lmcache");

        Assert.Equal(2, keys.Distinct().Count());
    }

    [Fact]
    public async Task 答案為空時不寫入快取()
    {
        _cache.SetupGet(c => c.IsEnabled).Returns(true);
        _cache.Setup(c => c.Get(It.IsAny<string>())).Returns((string?)null);

        await Create(Streaming(Body(Usage(10, 0))), withCache: true).GenerateFromTokensAsync(Tokens);

        _cache.Verify(c => c.Set(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>?>()), Times.Never);
    }

    [Fact]
    public async Task 快取關閉時完全不碰快取()
    {
        _cache.SetupGet(c => c.IsEnabled).Returns(false);

        await Create(Streaming(Body(Delta("好"))), withCache: true).GenerateFromTokensAsync(Tokens);

        _cache.Verify(c => c.Get(It.IsAny<string>()), Times.Never);
        _cache.Verify(c => c.Set(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>?>()), Times.Never);
    }
}
