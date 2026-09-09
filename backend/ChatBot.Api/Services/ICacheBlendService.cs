namespace ChatBot.Api.Services;

/// <summary>
/// CacheBlend 服務介面 - 用於智能混合快取內容和新計算內容
/// </summary>
public interface ICacheBlendService
{
    /// <summary>
    /// 是否啟用 CacheBlend
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 根據相似度和混合策略，混合快取內容和新的 LLM 回應
    /// </summary>
    /// <param name="cacheEntries">可用的快取項目列表</param>
    /// <param name="newResponse">新的 LLM 回應</param>
    /// <param name="requestContext">請求上下文（用於相似度計算）</param>
    /// <returns>混合後的回應內容</returns>
    string Blend(
        IEnumerable<CacheEntry> cacheEntries, 
        string newResponse, 
        BlendContext requestContext);

    /// <summary>
    /// 計算快取項目與當前請求的相似度分數
    /// </summary>
    double CalculateSimilarity(string currentPrompt, CacheEntry cacheEntry);

    /// <summary>
    /// 獲取 CacheBlend 統計資訊
    /// </summary>
    BlendStats GetStats();
}

/// <summary>
/// Blend 請求上下文
/// </summary>
public class BlendContext
{
    public string SystemPrompt { get; set; } = "";
    public string UserPrompt { get; set; } = "";
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// CacheBlend 統計資訊
/// </summary>
public class BlendStats
{
    public long BlendCount { get; set; }
    public long ExactHitCount { get; set; }
    public long FullComputeCount { get; set; }
    public double BlendEfficiency => BlendCount + FullComputeCount > 0 
        ? (double)BlendCount / (BlendCount + FullComputeCount) * 100 
        : 0;
}
