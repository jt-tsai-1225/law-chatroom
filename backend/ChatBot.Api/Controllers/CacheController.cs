using ChatBot.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Api.Controllers;

/// <summary>
/// 快取管理控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CacheController : ControllerBase
{
    private readonly ILMCacheService _cacheService;
    private readonly ICacheBlendService _blendService;
    private readonly ILogger<CacheController> _logger;

    public CacheController(
        ILMCacheService cacheService,
        ICacheBlendService blendService,
        ILogger<CacheController> logger)
    {
        _cacheService = cacheService;
        _blendService = blendService;
        _logger = logger;
    }

    /// <summary>
    /// 獲取 LMcache 統計資訊
    /// </summary>
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

    /// <summary>
    /// 清空 LMcache
    /// </summary>
    [HttpPost("lmcache/clear")]
    public ActionResult ClearLMCache()
    {
        _cacheService.Clear();
        _logger.LogInformation("LMcache 已透過 API 清空");
        
        return Ok(new { message = "LMcache cleared successfully" });
    }

    /// <summary>
    /// 獲取 CacheBlend 統計資訊
    /// </summary>
    [HttpGet("cacheblend/stats")]
    public ActionResult<object> GetCacheBlendStats()
    {
        var stats = _blendService.GetStats();
        
        return Ok(new
        {
            isEnabled = _blendService.IsEnabled,
            blendCount = stats.BlendCount,
            exactHitCount = stats.ExactHitCount,
            fullComputeCount = stats.FullComputeCount,
            blendEfficiency = Math.Round(stats.BlendEfficiency, 2)
        });
    }

    /// <summary>
    /// 獲取所有快取統計資訊
    /// </summary>
    [HttpGet("stats")]
    public ActionResult<object> GetAllStats()
    {
        var lmcacheStats = _cacheService.GetStats();
        var blendStats = _blendService.GetStats();
        
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
            cacheblend = new
            {
                isEnabled = _blendService.IsEnabled,
                blendCount = blendStats.BlendCount,
                exactHitCount = blendStats.ExactHitCount,
                fullComputeCount = blendStats.FullComputeCount,
                blendEfficiency = Math.Round(blendStats.BlendEfficiency, 2)
            }
        });
    }
}
