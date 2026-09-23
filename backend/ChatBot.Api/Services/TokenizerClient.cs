using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ChatBot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

/// <summary>
/// 呼叫 vLLM 內建的 /tokenize 端點，把文字轉成 token id。
///
/// 為什麼不在 C# 端自己實作 tokenizer：
///   CacheBlend 以「片段的 token id 內容雜湊」作為快取鍵，只要 tokenize 結果
///   與寫入快取時差一個 token，就會完全不命中。直接問推論引擎本身是唯一能
///   保證一致的作法。
///
/// add_special_tokens 一律用 false：
///   BOS（id=1）由 PromptBuilder 在最前面統一補上，各片段不能各自帶 BOS。
/// </summary>
public interface ITokenizerClient
{
    Task<int[]> TokenizeAsync(string text, bool addSpecialTokens = false,
                              CancellationToken cancellationToken = default);

    /// <summary>對固定不變的文字（系統提示詞、分隔符、條文片段）做記憶體快取。</summary>
    Task<int[]> TokenizeCachedAsync(string text, bool addSpecialTokens = false,
                                    CancellationToken cancellationToken = default);
}

public class TokenizerClient : ITokenizerClient
{
    private readonly HttpClient _httpClient;
    private readonly RAGSettings _settings;
    private readonly ILogger<TokenizerClient> _logger;
    private readonly ConcurrentDictionary<string, int[]> _cache = new();

    public TokenizerClient(
        IHttpClientFactory httpClientFactory,
        IOptions<RAGSettings> settings,
        ILogger<TokenizerClient> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _logger = logger;
    }

    private sealed class TokenizeRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
        [JsonPropertyName("add_special_tokens")] public bool AddSpecialTokens { get; set; }
    }

    private sealed class TokenizeResponse
    {
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("tokens")] public int[]? Tokens { get; set; }
    }

    public async Task<int[]> TokenizeAsync(
        string text, bool addSpecialTokens = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<int>();

        var url = $"{_settings.LlmBaseUrl.TrimEnd('/')}/tokenize";
        var payload = new TokenizeRequest
        {
            Model = _settings.LlmModel,
            Prompt = text,
            AddSpecialTokens = addSpecialTokens
        };

        using var response = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"/tokenize 回傳 {(int)response.StatusCode}：{body}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<TokenizeResponse>(
            cancellationToken: cancellationToken);

        if (parsed?.Tokens is null)
        {
            throw new InvalidOperationException("/tokenize 回應沒有 tokens 欄位");
        }

        return parsed.Tokens;
    }

    public async Task<int[]> TokenizeCachedAsync(
        string text, bool addSpecialTokens = false, CancellationToken cancellationToken = default)
    {
        var key = (addSpecialTokens ? "T|" : "F|") + text;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var tokens = await TokenizeAsync(text, addSpecialTokens, cancellationToken);
        _cache[key] = tokens;

        if (_cache.Count % 50 == 0)
        {
            _logger.LogDebug("TokenizerClient 快取筆數: {Count}", _cache.Count);
        }
        return tokens;
    }
}
