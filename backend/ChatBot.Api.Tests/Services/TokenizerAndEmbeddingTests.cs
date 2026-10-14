using System.Net;
using System.Text.Json;

namespace ChatBot.Api.Tests.Services;

public class TokenizerClientTests
{
    private static TokenizerClient Create(
        StubHttpMessageHandler handler, Action<RAGSettings>? configure = null)
        => new(
            TestFactory.HttpClientFactory(handler).Object,
            TestFactory.Options(s =>
            {
                s.LlmBaseUrl = "http://llm:8000/";
                s.LlmModel = "test-model";
                configure?.Invoke(s);
            }),
            NullLogger<TokenizerClient>.Instance);

    private static StubHttpMessageHandler Returning(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(_ => StubHttpMessageHandler.Json(json, status));

    [Fact]
    public async Task 空字串直接回空陣列_不打網路()
    {
        var handler = Returning("{}");
        var client = Create(handler);

        var tokens = await client.TokenizeAsync("");

        Assert.Empty(tokens);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 送出_tokenize_請求並回傳_tokens()
    {
        var handler = Returning("""{"count":3,"tokens":[5,6,7]}""");
        var client = Create(handler);

        var tokens = await client.TokenizeAsync("你好", addSpecialTokens: true);

        Assert.Equal(new[] { 5, 6, 7 }, tokens);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://llm:8000/tokenize", request.RequestUri!.ToString());

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("你好", body.RootElement.GetProperty("prompt").GetString());
        Assert.True(body.RootElement.GetProperty("add_special_tokens").GetBoolean());
    }

    [Fact]
    public async Task 引擎回錯誤碼時丟出含狀態碼與內容的例外()
    {
        var client = Create(new StubHttpMessageHandler(
            _ => StubHttpMessageHandler.Text("model not found", HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.TokenizeAsync("x"));

        Assert.Contains("404", ex.Message);
        Assert.Contains("model not found", ex.Message);
    }

    [Fact]
    public async Task 回應沒有_tokens_欄位時丟出例外()
    {
        var client = Create(Returning("""{"count":0}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.TokenizeAsync("x"));
    }

    [Fact]
    public async Task TokenizeCached_同樣的文字只打一次網路()
    {
        var handler = Returning("""{"count":1,"tokens":[9]}""");
        var client = Create(handler);

        var first = await client.TokenizeCachedAsync("條文");
        var second = await client.TokenizeCachedAsync("條文");

        Assert.Equal(new[] { 9 }, first);
        Assert.Same(first, second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TokenizeCached_是否加特殊_token_是不同的快取項目()
    {
        var handler = Returning("""{"count":1,"tokens":[9]}""");
        var client = Create(handler);

        await client.TokenizeCachedAsync("條文", addSpecialTokens: false);
        await client.TokenizeCachedAsync("條文", addSpecialTokens: true);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TokenizeCached_累積到五十筆時仍正常運作()
    {
        var handler = Returning("""{"count":1,"tokens":[1]}""");
        var client = Create(handler);

        for (var i = 0; i < 50; i++)
        {
            await client.TokenizeCachedAsync($"文字-{i}");
        }

        Assert.Equal(50, handler.Requests.Count);
    }
}

public class EmbeddingServiceTests
{
    private static (EmbeddingService Service, StubHttpMessageHandler Handler, Mock<IHttpClientFactory> Factory)
        Create(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(responseJson, status));
        var factory = TestFactory.HttpClientFactory(handler);
        var service = new EmbeddingService(
            factory.Object,
            TestFactory.Options(s =>
            {
                s.EmbeddingEndpoint = "http://embed.test/v1/embeddings";
                s.EmbeddingModel = "embed-model";
                s.EmbeddingApiKey = "secret-key";
            }));

        return (service, handler, factory);
    }

    [Fact]
    public async Task 解析回應中的向量()
    {
        var (service, _, _) = Create("""{"data":[{"embedding":[0.5,1.5,-2.0]}]}""");

        var vector = await service.GenerateEmbeddingAsync("公司");

        Assert.Equal(new[] { 0.5f, 1.5f, -2.0f }, vector);
    }

    [Fact]
    public async Task 請求帶上端點_模型_輸入與_Bearer_金鑰()
    {
        var (service, handler, _) = Create("""{"data":[{"embedding":[1.0]}]}""");

        await service.GenerateEmbeddingAsync("公司");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://embed.test/v1/embeddings", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("secret-key", request.Headers.Authorization!.Parameter);

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal("embed-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("公司", body.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public async Task 必須使用具名的_Embedding_client_才能套用三十秒逾時()
    {
        var (service, _, factory) = Create("""{"data":[{"embedding":[1.0]}]}""");

        await service.GenerateEmbeddingAsync("x");

        factory.Verify(f => f.CreateClient("Embedding"), Times.Once);
        factory.Verify(f => f.CreateClient(It.Is<string>(n => n != "Embedding")), Times.Never);
    }

    [Fact]
    public async Task 端點回錯誤碼時丟出_HttpRequestException()
    {
        var (service, _, _) = Create("{}", HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GenerateEmbeddingAsync("x"));
    }

    [Fact]
    public async Task 批次版一個文字打一次請求並保持順序()
    {
        var (service, handler, _) = Create("""{"data":[{"embedding":[1.0,2.0]}]}""");

        var vectors = await service.GenerateEmbeddingsAsync(new[] { "甲", "乙", "丙" });

        Assert.Equal(3, vectors.Length);
        Assert.All(vectors, v => Assert.Equal(new[] { 1.0f, 2.0f }, v));
        Assert.Equal(3, handler.Requests.Count);

        // 序列化預設會把中文轉成 \uXXXX，所以解析後再比，不要直接比字串
        var inputs = handler.RequestBodies
            .Select(b => JsonDocument.Parse(b).RootElement.GetProperty("input").GetString())
            .ToList();
        Assert.Equal(new[] { "甲", "乙", "丙" }, inputs);
    }
}
