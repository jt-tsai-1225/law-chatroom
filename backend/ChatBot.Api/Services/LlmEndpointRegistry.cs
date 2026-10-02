using ChatBot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

/// <summary>一個快取模式目前的狀態。</summary>
public sealed class CacheModeStatus
{
    public required string Mode { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>有沒有設定端點。false 表示這個模式根本沒部署。</summary>
    public bool Configured { get; init; }

    /// <summary>最近一次探測是否通。</summary>
    public bool Reachable { get; init; }

    /// <summary>端點位址。除錯用，前端不必顯示。</summary>
    public string? BaseUrl { get; init; }

    /// <summary>不可用時的說明，可直接顯示給使用者。</summary>
    public string? Note { get; init; }
}

public interface ILlmEndpointRegistry
{
    /// <summary>取得該模式的端點位址；沒設定時回 null。</summary>
    string? ResolveBaseUrl(string mode);

    /// <summary>三種模式目前的狀態，含可用性探測。</summary>
    Task<List<CacheModeStatus>> GetStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// 快取模式 → vLLM 端點的對應。
///
/// 可用性是**探測**出來的，不是設定出來的：設定裡寫了端點不代表那個
/// 容器正在跑。單卡上三個實例放不下，所以「無快取」平常是停的，
/// 介面必須據實顯示它現在能不能用，而不是列出來讓使用者點了才失敗。
///
/// 探測結果快取數秒，避免每次開啟頁面都打三個健康檢查。
/// </summary>
public sealed class LlmEndpointRegistry : ILlmEndpointRegistry
{
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(15);

    private readonly RAGSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlmEndpointRegistry> _logger;

    private readonly Dictionary<string, (DateTime At, bool Ok)> _probeCache = new();
    private readonly SemaphoreSlim _probeLock = new(1, 1);

    public LlmEndpointRegistry(
        IOptions<RAGSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<LlmEndpointRegistry> logger)
    {
        _settings = settings.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string? ResolveBaseUrl(string mode)
    {
        // 未設定對應表時，CacheBlend 退回原本的單一端點設定，
        // 讓既有部署在沒有改設定的情況下照常運作。
        if (_settings.LlmEndpoints is null || _settings.LlmEndpoints.Count == 0)
        {
            return mode == CacheModes.CacheBlend ? _settings.LlmBaseUrl : null;
        }

        if (_settings.LlmEndpoints.TryGetValue(mode, out var url)
            && !string.IsNullOrWhiteSpace(url))
        {
            return url.TrimEnd('/');
        }

        return mode == CacheModes.CacheBlend ? _settings.LlmBaseUrl : null;
    }

    public async Task<List<CacheModeStatus>> GetStatusAsync(CancellationToken ct = default)
    {
        var result = new List<CacheModeStatus>(CacheModes.All.Count);

        foreach (var mode in CacheModes.All)
        {
            var baseUrl = ResolveBaseUrl(mode);

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                result.Add(new CacheModeStatus
                {
                    Mode = mode,
                    DisplayName = CacheModes.DisplayName(mode),
                    Configured = false,
                    Reachable = false,
                    Note = "未設定端點"
                });
                continue;
            }

            var ok = await ProbeAsync(baseUrl, ct);

            result.Add(new CacheModeStatus
            {
                Mode = mode,
                DisplayName = CacheModes.DisplayName(mode),
                Configured = true,
                Reachable = ok,
                BaseUrl = baseUrl,
                Note = ok ? null : "端點目前沒有回應，可能尚未啟動"
            });
        }

        return result;
    }

    private async Task<bool> ProbeAsync(string baseUrl, CancellationToken ct)
    {
        await _probeLock.WaitAsync(ct);
        try
        {
            if (_probeCache.TryGetValue(baseUrl, out var cached)
                && DateTime.UtcNow - cached.At < ProbeTtl)
            {
                return cached.Ok;
            }
        }
        finally
        {
            _probeLock.Release();
        }

        bool ok;
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            using var response = await client.GetAsync($"{baseUrl}/v1/models", ct);
            ok = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "端點 {Url} 探測失敗", baseUrl);
            ok = false;
        }

        await _probeLock.WaitAsync(ct);
        try
        {
            _probeCache[baseUrl] = (DateTime.UtcNow, ok);
        }
        finally
        {
            _probeLock.Release();
        }

        return ok;
    }
}
