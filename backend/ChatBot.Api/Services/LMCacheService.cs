using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ChatBot.Api.Configuration;

namespace ChatBot.Api.Services;

/// <summary>
/// LMcache 服務實作 - 基於記憶體的本地快取系統
/// 用於儲存和檢索 LLM 輸出，減少重複 API 呼叫
/// 
/// 設計原則：
/// 1. 使用內容雜湊作為快取鍵，確保精確匹配
/// 2. 支援 TTL (Time To Live) 過期機制
/// 3. 支援 LRU (Least Recently Used) .eviction 策略
/// 4. 線程安全的併發訪問
/// </summary>
public class LMCacheService : ILMCacheService
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache;
    private readonly int _maxEntries;
    private readonly int _defaultTtlMinutes;
    private readonly bool _enabled;
    private readonly ILogger<LMCacheService> _logger;
    private readonly object _lockObject = new object();
    
    // 統計資訊
    private long _hitCount = 0;
    private long _missCount = 0;

    public LMCacheService(
        Microsoft.Extensions.Options.IOptions<RAGSettings>? ragSettings = null,
        ILogger<LMCacheService>? logger = null)
    {
        _cache = new ConcurrentDictionary<string, CacheEntry>();
        _logger = logger ?? LoggerFactory.Create(builder => { }).CreateLogger<LMCacheService>();
        
        _enabled = ragSettings?.Value.LmcacheEnabled ?? true;
        _maxEntries = ragSettings?.Value.LmcacheMaxEntries ?? 1000;
        _defaultTtlMinutes = ragSettings?.Value.LmcacheDefaultTtlMinutes ?? 60;
        
        _logger.LogInformation("LMCache 初始化完成 - 啟用: {Enabled}, 最大項目數: {MaxEntries}, TTL: {Ttl}分鐘",
            _enabled, _maxEntries, _defaultTtlMinutes);
    }

    public bool IsEnabled => _enabled && _maxEntries > 0;

    public long HitCount => Interlocked.Read(ref _hitCount);

    public long MissCount => Interlocked.Read(ref _missCount);

    /// <summary>
    /// 根據 prompt 內容生成快取鍵（SHA256 雜湊）
    /// </summary>
    public static string GenerateCacheKey(string systemPrompt, string userPrompt)
    {
        var content = $"{systemPrompt}|{userPrompt}";
        return HashString(content);
    }

    /// <summary>
    /// 根據已有快取鍵獲取回應
    /// </summary>
    public string? Get(string cacheKey)
    {
        if (!IsEnabled)
        {
            Interlocked.Increment(ref _missCount);
            _logger.LogDebug("LMCache 未啟用，快取未命中");
            return null;
        }

        if (_cache.TryGetValue(cacheKey, out var entry))
        {
            // 檢查是否過期
            if (entry.CreatedAt.AddMinutes(_defaultTtlMinutes) < DateTime.UtcNow)
            {
                _cache.TryRemove(cacheKey, out _);
                Interlocked.Increment(ref _missCount);
                _logger.LogDebug("快取項目已過期: {CacheKey}", cacheKey);
                return null;
            }

            // 更新訪問統計
            entry.AccessCount++;
            entry.CreatedAt = DateTime.UtcNow;  // 更新時間以延長 TTL
            
            Interlocked.Increment(ref _hitCount);
            _logger.LogDebug("LMCache 命中: {CacheKey}", cacheKey);
            return entry.Value;
        }

        Interlocked.Increment(ref _missCount);
        _logger.LogDebug("LMCache 未命中: {CacheKey}", cacheKey);
        return null;
    }

    /// <summary>
    /// 將 LLM 回應儲存到快取
    /// </summary>
    public void Set(string cacheKey, string response, Dictionary<string, object>? metadata = null, string? sourcePrompt = null)
    {
        if (!IsEnabled)
        {
            _logger.LogDebug("LMCache 未啟用，跳過儲存");
            return;
        }

        if (string.IsNullOrEmpty(cacheKey) || string.IsNullOrEmpty(response))
        {
            _logger.LogWarning("無法儲存空快取項目");
            return;
        }

        // 如果快取已滿，需要移除舊項目（LRU 策略）
        if (_cache.Count >= _maxEntries)
        {
            EvictOldestEntry();
        }

        var entry = new CacheEntry
        {
            Key = cacheKey,
            Value = response,
            CreatedAt = DateTime.UtcNow,
            Metadata = metadata,
            SourcePrompt = sourcePrompt
        };

        _cache[cacheKey] = entry;
        
        var responseLength = Encoding.UTF8.GetByteCount(response);
        _logger.LogInformation("LMCache 儲存成功 - 鍵: {CacheKey}, 大小: {Size}KB", 
            cacheKey, responseLength / 1024);
    }

    public bool Contains(string cacheKey)
    {
        if (!IsEnabled) return false;
        return _cache.ContainsKey(cacheKey);
    }

    public void Remove(string cacheKey)
    {
        if (!IsEnabled) return;
        
        _cache.TryRemove(cacheKey, out _);
        _logger.LogDebug("LMCache 移除項目: {CacheKey}", cacheKey);
    }

    public void Clear()
    {
        if (!IsEnabled) return;
        
        lock (_lockObject)
        {
            _cache.Clear();
            Interlocked.Exchange(ref _hitCount, 0);
            Interlocked.Exchange(ref _missCount, 0);
        }
        
        _logger.LogInformation("LMCache 已清空");
    }

    public CacheStats GetStats()
    {
        var totalSize = _cache.Values.Sum(e => Encoding.UTF8.GetByteCount(e.Value));
        
        return new CacheStats
        {
            HitCount = HitCount,
            MissCount = MissCount,
            TotalEntries = _cache.Count,
            TotalSizeBytes = (long)totalSize
        };
    }

    /// <summary>
    /// 移除最舊的快取項目（LRU 策略）
    /// </summary>
    private void EvictOldestEntry()
    {
        lock (_lockObject)
        {
            var oldestKey = _cache.Values
                .OrderBy(e => e.CreatedAt)
                .FirstOrDefault()?.Key;

            if (oldestKey != null && _cache.TryRemove(oldestKey, out _))
            {
                _logger.LogDebug("LMCache 移除最舊項目以騰出空間: {CacheKey}", oldestKey);
            }
        }
    }

    /// <summary>
    /// 使用 SHA256 生成字串雜湊
    /// </summary>
    private static string HashString(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = sha256.ComputeHash(bytes);
        
        // 轉為 hex 字串
        var sb = new StringBuilder();
        foreach (var b in hashBytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}

/// <summary>
/// LMcache 設定選項
/// </summary>
public class LMCacheSettings
{
    public const string SectionName = "LMCache";

    /// <summary>
    /// 是否啟用 LMcache
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 最大快取項目數量
    /// </summary>
    public int MaxEntries { get; set; } = 1000;

    /// <summary>
    /// 預設 TTL（分鐘）
    /// </summary>
    public int DefaultTtlMinutes { get; set; } = 60;

    /// <summary>
    /// 快取儲存類型 (memory/disk)
    /// </summary>
    public string StorageType { get; set; } = "memory";

    /// <summary>
    /// 如果 StorageType 是 disk，這是儲存路徑
    /// </summary>
    public string? StoragePath { get; set; }
}
