using System.Collections.Concurrent;

namespace ChatBot.Api.Services;

/// <summary>
/// LMcache 服務介面 - 用於快取和檢索 LLM 輸出
/// </summary>
public interface ILMCacheService
{
    /// <summary>
    /// 檢查快取是否啟用
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 獲取快取命中統計
    /// </summary>
    long HitCount { get; }

    /// <summary>
    /// 獲取快取未命中統計
    /// </summary>
    long MissCount { get; }

    /// <summary>
    /// 根據快取鍵獲取儲存的 LLM 回應
    /// </summary>
    /// <param name="cacheKey">快取鍵（由 prompt 內容生成）</param>
    /// <returns>如果命中快取則返回回應內容，否則為 null</returns>
    string? Get(string cacheKey);

    /// <summary>
    /// 將 LLM 回應儲存到快取
    /// </summary>
    /// <param name="cacheKey">快取鍵</param>
    /// <param name="response">LLM 回應內容</param>
    /// <param name="metadata">額外中繼資料（可選）</param>
    /// <param name="sourcePrompt">產生此回應的原始 prompt 文字（供 CacheBlend 相似度比對使用）</param>
    void Set(string cacheKey, string response, Dictionary<string, object>? metadata = null, string? sourcePrompt = null);

    /// <summary>
    /// 檢查快取鍵是否存在
    /// </summary>
    bool Contains(string cacheKey);

    /// <summary>
    /// 從快取中移除指定的項目
    /// </summary>
    void Remove(string cacheKey);

    /// <summary>
    /// 清空所有快取項目
    /// </summary>
    void Clear();

    /// <summary>
    /// 獲取快取統計資訊（命中率等）
    /// </summary>
    CacheStats GetStats();
}

/// <summary>
/// 快取項目
/// </summary>
public class CacheEntry
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public long AccessCount { get; set; } = 0;
    public Dictionary<string, object>? Metadata { get; set; }
    public string? Hash { get; set; }  // 用於快速匹配

    /// <summary>
    /// 產生此快取項目時使用的原始 prompt 文字（Key 只是它的 SHA256 雜湊，無法用於相似度比對）
    /// </summary>
    public string? SourcePrompt { get; set; }
}

/// <summary>
/// 快取統計資訊
/// </summary>
public class CacheStats
{
    public long HitCount { get; set; }
    public long MissCount { get; set; }
    public double HitRate => HitCount + MissCount > 0 ? (double)HitCount / (HitCount + MissCount) * 100 : 0;
    public long TotalEntries { get; set; }
    public long TotalSizeBytes { get; set; }
}
