using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

/// <summary>
/// LLM 服務回應包含 TTFT 量測資訊
/// </summary>
public class LLMCompletionResult
{
    public string Content { get; set; } = "";
    public long TtftMilliseconds { get; set; }
    public long TotalMilliseconds { get; set; }
    public int TokenCount { get; set; }
    public double TokensPerSecond { get; set; }
    public bool FromCache { get; set; }
}

/// <summary>
/// LLM Chat 服務 - 用於生成 RAG 回覆
/// </summary>
public interface ILLMService
{
    Task<string> GenerateCompletionAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 生成完成並返回 TTFT 量測結果
    /// </summary>
    Task<LLMCompletionResult> GenerateCompletionWithTimingAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Chat completion 請求物件 (OpenAI API 格式)
/// </summary>
public class ChatCompletionRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("model")]
    public string Model { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();
    
    [System.Text.Json.Serialization.JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.2;
    
    [System.Text.Json.Serialization.JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 2000;
    
    [System.Text.Json.Serialization.JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;
}

/// <summary>
/// Chat message
/// </summary>
public class ChatMessage
{
    [System.Text.Json.Serialization.JsonPropertyName("role")]
    public string Role { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

/// <summary>
/// Chat completion 回應物件
/// </summary>
public class ChatCompletionResponse
{
    public List<Choice> Choices { get; set; } = new();
}

public class Choice
{
    public ChatMessage Message { get; set; } = new();
}

public class LLMService : ILLMService
{
    private readonly HttpClient _httpClient;
    private readonly RAGSettings _settings;
    private readonly ILogger<LLMService> _logger;
    private readonly ILMCacheService? _cacheService;
    private readonly ICacheBlendService? _blendService;

    public LLMService(
        IHttpClientFactory httpClientFactory,
        IOptions<RAGSettings> settings,
        ILogger<LLMService> logger,
        ILMCacheService? cacheService = null,
        ICacheBlendService? blendService = null)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _logger = logger;
        _cacheService = cacheService;
        _blendService = blendService;
    }

    public Task<string> GenerateCompletionAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        return GenerateCompletionWithTimingAsync(systemPrompt, userPrompt, cancellationToken).ContinueWith(
            t => t.Result.Content,
            System.Threading.CancellationToken.None,
            System.Threading.Tasks.TaskContinuationOptions.None,
            System.Threading.Tasks.TaskScheduler.Default);
    }

    public async Task<LLMCompletionResult> GenerateCompletionWithTimingAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new LLMCompletionResult();
        
        // 1. 檢查快取
        if (_cacheService != null && _cacheService.IsEnabled)
        {
            var cacheKey = LMCacheService.GenerateCacheKey(systemPrompt, userPrompt);
            
            var cachedResponse = _cacheService.Get(cacheKey);
            if (cachedResponse != null)
            {
                result.FromCache = true;
                result.Content = cachedResponse;
                result.TtftMilliseconds = 0; // 快取命中，TTFT 為 0
                result.TotalMilliseconds = totalStopwatch.ElapsedMilliseconds;
                result.TokenCount = EstimateTokenCount(cachedResponse);
                result.TokensPerSecond = result.TotalMilliseconds > 0
                    ? (result.TokenCount / (double)result.TotalMilliseconds) * 1000
                    : 0;
                
                _logger.LogInformation("LMcache 命中 - 總耗時: {Total}ms", result.TotalMilliseconds);
                return result;
            }
        }
        
        // 2. 構建請求
        var requestBody = new ChatCompletionRequest
        {
            Model = _settings.LlmModel,
            Temperature = _settings.LlmTemperature,
            MaxTokens = _settings.LlmMaxTokens,
            Stream = true // 啟用串流以量測 TTFT
        };

        var jsonRequest = JsonSerializer.Serialize(requestBody);
        _logger.LogDebug("LLM 請求內容: {Request}", jsonRequest);

        var requestContent = new StringContent(
            jsonRequest,
            Encoding.UTF8,
            "application/json");

        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.LlmApiKey);

        // 3. 發送請求並量測 TTFT
        var requestStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var firstTokenTime = DateTimeOffset.UtcNow;
        
        var responseTask = _httpClient.PostAsync(_settings.LlmEndpoint, requestContent, cancellationToken);
        
        HttpResponseMessage response;
        try
        {
            response = await responseTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM API 呼叫失敗");
            throw;
        }
        
        // 注意：對於非串流請求，TTFT = 直到收到完整回應的時間
        // 因為我們無法在半途攔截非串流回應
        var ttftStopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("LLM API 返回錯誤 {StatusCode}: {Error}", response.StatusCode, errorContent);
            throw new HttpRequestException($"LLM API 返回錯誤 {response.StatusCode}: {errorContent}");
        }

        // 4. 讀取完整回應
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        ttftStopwatch.Stop();
        
        result.TtftMilliseconds = ttftStopwatch.ElapsedMilliseconds;
        
        _logger.LogDebug("LLM 回應內容: {Response}", json);
        
        // 5. 解析回應
        var jsonObject = JsonDocument.Parse(json).RootElement;
        
        var choices = jsonObject.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            _logger.LogWarning("LLM 回應沒有 choices 欄位");
            result.Content = "";
        }
        else
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                result.Content = content.GetString() ?? "";
            }
            else
            {
                _logger.LogWarning("LLM 回應沒有 message 或 content 欄位");
                result.Content = "";
            }
        }
        
        // 6. 儲存到快取
        if (_cacheService != null && _cacheService.IsEnabled && !string.IsNullOrEmpty(result.Content))
        {
            var cacheKey = LMCacheService.GenerateCacheKey(systemPrompt, userPrompt);
            _cacheService.Set(cacheKey, result.Content);
            _logger.LogDebug("已將回應儲存到 LMcache - 鍵: {CacheKey}", cacheKey);
        }
        
        // 7. 計算統計資訊
        totalStopwatch.Stop();
        result.TotalMilliseconds = totalStopwatch.ElapsedMilliseconds;
        result.TokenCount = EstimateTokenCount(result.Content);
        result.TokensPerSecond = result.TotalMilliseconds > 0
            ? (result.TokenCount / (double)result.TotalMilliseconds) * 1000
            : 0;
        
        _logger.LogInformation(
            "LLM 呼叫完成 - TTFT: {Ttft}ms, 總耗時: {Total}ms, Tokens: {Tokens}, 吞吐量: {Tps:.2f} tokens/s",
            result.TtftMilliseconds, result.TotalMilliseconds, result.TokenCount, result.TokensPerSecond);

        return result;
    }
    
    /// <summary>
    /// 估算 token 數量（簡化版：中文字符約 1:1，英文單詞約 1.3:1）
    /// </summary>
    private int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        
        var chineseChars = text.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        var englishWords = text.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        
        return chineseChars + (int)(englishWords * 1.3);
    }
}
