using ChatBot.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Api.Controllers;

/// <summary>
/// 快取管理控制器。
///
/// 注意：這裡管的是 C# 層的「回答字串」快取，與推論引擎內的 KV 快取
/// （LMCache / CacheBlend）無關。CacheBlend 的命中率由每次回答的
/// ChatResponse.CachedTokens 回報，不在這裡。
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CacheController : ControllerBase
{
    private readonly ILMCacheService _cacheService;
    private readonly IChunkCacheTracker _chunkTracker;
    private readonly ILogger<CacheController> _logger;

    public CacheController(
        ILMCacheService cacheService,
        IChunkCacheTracker chunkTracker,
        ILogger<CacheController> logger)
    {
        _cacheService = cacheService;
        _chunkTracker = chunkTracker;
        _logger = logger;
    }

    /// <summary>獲取 C# 回答快取的統計資訊</summary>
    [HttpGet("lmcache/stats")]
    public ActionResult<object> GetLMCacheStats()
    {
        var stats = _cacheService.GetStats();

        return Ok(new
        {
            isEnabled = _cacheService.IsEnabled,
            hitCount = stats.HitCount,
            missCount = stats.MissCount,
            hitRate = Math.Round(stats.HitRate, 2),
            totalEntries = stats.TotalEntries,
            totalSizeBytes = stats.TotalSizeBytes,
            totalSizeKB = Math.Round(stats.TotalSizeBytes / 1024.0, 2)
        });
    }

    /// <summary>清空 C# 回答快取</summary>
    [HttpPost("lmcache/clear")]
    public ActionResult ClearLMCache()
    {
        _cacheService.Clear();
        _logger.LogInformation("C# 回答快取已透過 API 清空");

        return Ok(new { message = "LMcache cleared successfully" });
    }

    /// <summary>
    /// 清空「片段已送出」的記錄。
    ///
    /// 量測用：推論引擎重啟後 KV 快取是空的，但這份記錄還留著舊資料，
    /// 會把實際上沒命中的片段排到前面。做 A/B 量測前先呼叫這支。
    /// </summary>
    [HttpPost("chunks/clear")]
    public ActionResult ClearChunkTracker()
    {
        var before = _chunkTracker.Count;
        _chunkTracker.Clear();
        _logger.LogInformation("片段快取記錄已透過 API 清空（原有 {N} 筆）", before);

        return Ok(new { message = "chunk tracker cleared", clearedEntries = before });
    }

    /// <summary>獲取所有快取統計資訊</summary>
    [HttpGet("stats")]
    public ActionResult<object> GetAllStats()
    {
        var lmcacheStats = _cacheService.GetStats();

        return Ok(new
        {
            lmcache = new
            {
                isEnabled = _cacheService.IsEnabled,
                hitCount = lmcacheStats.HitCount,
                missCount = lmcacheStats.MissCount,
                hitRate = Math.Round(lmcacheStats.HitRate, 2),
                totalEntries = lmcacheStats.TotalEntries,
                totalSizeBytes = lmcacheStats.TotalSizeBytes
            },
            chunkTracker = new
            {
                trackedChunks = _chunkTracker.Count
            }
        });
    }
}
