using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

/// <summary>一次生成的結果，含判讀 CacheBlend 命中率所需的欄位。</summary>
public class LLMCompletionResult
{
    public string Content { get; set; } = "";

    /// <summary>真正的 TTFT：送出請求到收到第一個有內容的串流事件。</summary>
    public long TtftMilliseconds { get; set; }

    public long TotalMilliseconds { get; set; }
    public int TokenCount { get; set; }
    public double TokensPerSecond { get; set; }

    /// <summary>是否由 C# 層的回答快取直接回傳（會使 TTFT 失去意義，見 RAGSettings.LmcacheEnabled）。</summary>
    public bool FromCache { get; set; }

    /// <summary>引擎回報的 prompt 長度。</summary>
    public int PromptTokens { get; set; }

    /// <summary>引擎回報的 KV 快取命中 token 數，需 vLLM 啟動時帶 --enable-prompt-tokens-details。</summary>
    public int CachedTokens { get; set; }

    public double CacheHitRate => PromptTokens > 0 ? (double)CachedTokens / PromptTokens * 100 : 0;
}

public interface ILLMService
{
    /// <summary>
    /// 以 token id 陣列送出生成請求。
    ///
    /// 為什麼不是送文字：CacheBlend 需要在片段之間插入精確的分隔符 token，
    /// 若送字串由伺服端 tokenize，分隔符會與相鄰文字合併成不同的 token，
    /// 片段邊界就對不上、快取永遠不命中。組裝的工作在 PromptBuilder。
    /// </summary>
    /// <param name="maxTokens">
    /// 覆寫這一次的生成長度上限；省略時使用 RAGSettings.LlmMaxTokens。
    /// KV 預熱會傳 1——預熱只需要引擎把 prompt 算過一遍好把 KV 寫進快取，
    /// 生成出來的字會被丟棄，讓它產生完整答案是純粹的浪費。
    /// </param>
    /// <param name="cacheMode">
    /// 要打哪一個推論端點（見 CacheModes）。省略時用 CacheBlend。
    /// 指定的模式沒有設定端點時會拋 <see cref="CacheModeUnavailableException"/>，
    /// 不會靜默退回預設——否則使用者以為在比較兩種快取，實際打的是同一個引擎。
    /// </param>
    Task<LLMCompletionResult> GenerateFromTokensAsync(
        IReadOnlyList<int> promptTokens,
        CancellationToken cancellationToken = default,
        int? maxTokens = null,
        string? cacheMode = null);
}

/// <summary>指定的快取模式沒有可用的端點。對應 HTTP 503。</summary>
public sealed class CacheModeUnavailableException : Exception
{
    public string Mode { get; }

    public CacheModeUnavailableException(string mode, string message) : base(message)
    {
        Mode = mode;
    }
}

public class LLMService : ILLMService
{
    private readonly HttpClient _httpClient;
    private readonly RAGSettings _settings;
    private readonly ILogger<LLMService> _logger;
    private readonly ILMCacheService? _cacheService;
    private readonly ILlmEndpointRegistry _endpoints;

    public LLMService(
        IHttpClientFactory httpClientFactory,
        IOptions<RAGSettings> settings,
        ILlmEndpointRegistry endpoints,
        ILogger<LLMService> logger,
        ILMCacheService? cacheService = null)
    {
        _settings = settings.Value;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(_settings.LlmTimeoutSeconds);
        _endpoints = endpoints;
        _logger = logger;
        _cacheService = cacheService;
    }

    private sealed class StreamOptions
    {
        [JsonPropertyName("include_usage")] public bool IncludeUsage { get; set; } = true;
    }

    private sealed class CompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("prompt")] public int[] Prompt { get; set; } = Array.Empty<int>();
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("stream")] public bool Stream { get; set; } = true;
        [JsonPropertyName("stream_options")] public StreamOptions StreamOptions { get; set; } = new();
    }

    public async Task<LLMCompletionResult> GenerateFromTokensAsync(
        IReadOnlyList<int> promptTokens,
        CancellationToken cancellationToken = default,
        int? maxTokens = null,
        string? cacheMode = null)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new LLMCompletionResult();

        var mode = CacheModes.Normalize(cacheMode);
        var baseUrl = _endpoints.ResolveBaseUrl(mode);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new CacheModeUnavailableException(mode,
                $"「{CacheModes.DisplayName(mode)}」沒有設定推論端點。" +
                "單張卡放不下三個模型實例，預設只常駐 CacheBlend 與純 LMCache；" +
                "需要這個模式請先由管理者啟動對應的 vLLM 實例。");
        }

        // C# 層的回答快取。預設關閉——它一命中就不會送到推論引擎，TTFT 記成 0，
        // 量到的不是 CacheBlend 省下的時間。見 RAGSettings.LmcacheEnabled 的說明。
        string? cacheKey = null;
        if (_cacheService is { IsEnabled: true })
        {
            // 鍵要含快取模式，否則切換模式時會拿到另一個引擎的舊答案，
            // 使用者以為在比較兩種快取，看到的卻是同一筆回應。
            cacheKey = LMCacheService.GenerateCacheKey(
                string.Join(',', promptTokens.Take(64)),
                $"{promptTokens.Count}:{mode}");

            var cached = _cacheService.Get(cacheKey);
            if (cached != null)
            {
                totalStopwatch.Stop();
                result.FromCache = true;
                result.Content = cached;
                result.TtftMilliseconds = 0;
                result.TotalMilliseconds = totalStopwatch.ElapsedMilliseconds;
                result.TokenCount = EstimateTokenCount(cached);
                _logger.LogInformation("C# 回答快取命中，未送往推論引擎（TTFT 不具參考價值）");
                return result;
            }
        }

        var url = $"{baseUrl.TrimEnd('/')}/v1/completions";
        var body = new CompletionRequest
        {
            Model = _settings.LlmModel,
            Prompt = promptTokens.ToArray(),
            MaxTokens = maxTokens ?? _settings.LlmMaxTokens,
            Temperature = _settings.LlmTemperature
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrEmpty(_settings.LlmApiKey))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.LlmApiKey);
        }

        var requestStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // ResponseHeadersRead 是量到真實 TTFT 的關鍵：預設的 ResponseContentRead
        // 會等整個回應收完才返回，那樣量到的其實是總耗時。
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("推論引擎回傳 {Status}：{Error}", (int)response.StatusCode, error);
            throw new HttpRequestException($"推論引擎回傳 {(int)response.StatusCode}：{error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var builder = new StringBuilder();
        long? ttft = null;
        var generatedTokens = 0;

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") break;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                root = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "無法解析串流事件，已略過：{Payload}", Truncate(payload, 200));
                continue;
            }

            // usage 只出現在最後一筆，需要 stream_options.include_usage
            if (root.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) &&
                    pt.ValueKind == JsonValueKind.Number)
                {
                    result.PromptTokens = pt.GetInt32();
                }

                if (usage.TryGetProperty("prompt_tokens_details", out var details) &&
                    details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("cached_tokens", out var ct) &&
                    ct.ValueKind == JsonValueKind.Number)
                {
                    result.CachedTokens = ct.GetInt32();
                }
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("text", out var textElement)) continue;

                var text = textElement.GetString();
                if (string.IsNullOrEmpty(text)) continue;

                ttft ??= requestStopwatch.ElapsedMilliseconds;
                builder.Append(text);
                generatedTokens++;
            }
        }

        totalStopwatch.Stop();

        result.Content = builder.ToString().Trim();
        result.TtftMilliseconds = ttft ?? totalStopwatch.ElapsedMilliseconds;
        result.TotalMilliseconds = totalStopwatch.ElapsedMilliseconds;
        result.TokenCount = generatedTokens > 0 ? generatedTokens : EstimateTokenCount(result.Content);
        result.TokensPerSecond = result.TotalMilliseconds > 0
            ? result.TokenCount / (double)result.TotalMilliseconds * 1000
            : 0;

        if (result.PromptTokens == 0)
        {
            _logger.LogWarning(
                "回應沒有 usage.prompt_tokens，無法判讀快取命中率。" +
                "請確認 vLLM 啟動時帶了 --enable-prompt-tokens-details");
        }

        _logger.LogInformation(
            "生成完成 — TTFT: {Ttft}ms, 總耗時: {Total}ms, 產生 {Gen} tokens, " +
            "prompt {Prompt} tokens, 快取命中 {Cached}（{Rate:F1}%）",
            result.TtftMilliseconds, result.TotalMilliseconds, result.TokenCount,
            result.PromptTokens, result.CachedTokens, result.CacheHitRate);

        if (cacheKey != null && _cacheService is { IsEnabled: true } &&
            !string.IsNullOrEmpty(result.Content))
        {
            _cacheService.Set(cacheKey, result.Content);
        }

        return result;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>串流事件數不可得時的後備估算（中文字約 1:1，英文詞約 1.3:1）。</summary>
    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var chineseChars = text.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        var englishWords = text.Split(
            new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

        return chineseChars + (int)(englishWords * 1.3);
    }
}
