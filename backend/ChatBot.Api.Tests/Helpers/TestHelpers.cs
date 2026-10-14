using System.Net;
using System.Text;
using System.Text.Json;

namespace ChatBot.Api.Tests.Helpers;

/// <summary>
/// 假的 HttpMessageHandler：由測試決定每個請求的回應，並記下收到的請求與內容。
/// 單元測試不該真的打網路，這是所有 HttpClient 相依類別共用的替身。
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> RequestBodies { get; } = new();

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : this((request, _) => Task.FromResult(handler(request)))
    {
    }

    public StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken));

        return await _handler(request, cancellationToken);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(string text, HttpStatusCode status)
        => new(status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    public static HttpResponseMessage Sse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
}

public static class TestFactory
{
    /// <summary>不論要哪個具名 client，都回傳掛上指定 handler 的 HttpClient。</summary>
    public static Mock<IHttpClientFactory> HttpClientFactory(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    /// <summary>以 RAGSettings 預設值為底，套上測試需要的覆寫。</summary>
    public static RAGSettings Settings(Action<RAGSettings>? configure = null)
    {
        var settings = new RAGSettings();
        configure?.Invoke(settings);
        return settings;
    }

    public static IOptions<RAGSettings> Options(Action<RAGSettings>? configure = null)
        => Microsoft.Extensions.Options.Options.Create(Settings(configure));

    /// <summary>把 Controller 回傳的匿名物件轉成 JsonElement，方便以欄位名稱斷言。</summary>
    public static JsonElement ToJson(object? value)
        => JsonSerializer.SerializeToElement(value);
}

/// <summary>
/// 假的 tokenizer：沒有特別指定時，一個字元就是一個 token（值為字元碼）。
/// 讓測試能精確預測 prompt 的 token 數與內容，而不必依賴真實的 vLLM /tokenize。
/// </summary>
public sealed class FakeTokenizer : ITokenizerClient
{
    public Dictionary<string, int[]> Overrides { get; } = new();

    /// <summary>TokenizeCachedAsync 收到的文字（依呼叫順序）。</summary>
    public List<string> CachedCalls { get; } = new();

    /// <summary>TokenizeAsync 收到的文字（依呼叫順序）。</summary>
    public List<string> Calls { get; } = new();

    private int[] Map(string text)
        => Overrides.TryGetValue(text, out var tokens)
            ? tokens
            : text.Select(c => (int)c).ToArray();

    public Task<int[]> TokenizeAsync(
        string text, bool addSpecialTokens = false, CancellationToken cancellationToken = default)
    {
        Calls.Add(text);
        return Task.FromResult(Map(text));
    }

    public Task<int[]> TokenizeCachedAsync(
        string text, bool addSpecialTokens = false, CancellationToken cancellationToken = default)
    {
        CachedCalls.Add(text);
        return Task.FromResult(Map(text));
    }
}

/// <summary>記錄每次 Report 當下的值（WarmupProgress 是同一個物件反覆回報，要在當下取值）。</summary>
public sealed class RecordingProgress : IProgress<WarmupProgress>
{
    public List<(int Batches, int Processed, int Failed)> Reports { get; } = new();

    public void Report(WarmupProgress value)
        => Reports.Add((value.Batches, value.ProcessedChunks, value.FailedBatches));
}
