using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ChatBot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

/// <summary>
/// 記錄哪些條文片段已經送給推論引擎算過，用來把「可能已在快取中」的片段排到前面。
///
/// 為什麼需要這個：
///   LMCache 的 lookup 一遇到未命中的片段就會停止，後面的片段即使在快取裡也
///   不會被使用（驗證報告 8.3）。實測 Q4 的例子——第 287 條明明算過，卻因為
///   排在第 5 位、而第 1 位是全新的第 73 條，整串 lookup 在第一段就停住，
///   cached_tokens 只剩系統提示詞的 142。
///
/// 這份記錄是「樂觀估計」而非事實：
///   引擎那邊會因為容量壓力淘汰片段，這裡不會知道。估錯的代價只是少命中一些，
///   不會產生錯誤結果，所以不需要與引擎同步。
/// </summary>
public interface IChunkCacheTracker
{
    bool IsLikelyCached(string segmentText);
    void MarkSent(IEnumerable<string> segmentTexts);
    int Count { get; }
    void Clear();
}

public class ChunkCacheTracker : IChunkCacheTracker
{
    private readonly ConcurrentDictionary<string, long> _seen = new();
    private readonly int _capacity;
    private readonly ILogger<ChunkCacheTracker> _logger;
    private long _tick;

    public ChunkCacheTracker(
        IOptions<RAGSettings> settings,
        ILogger<ChunkCacheTracker> logger)
    {
        _capacity = Math.Max(16, settings.Value.ChunkTrackerCapacity);
        _logger = logger;
    }

    public int Count => _seen.Count;

    public bool IsLikelyCached(string segmentText)
        => _seen.ContainsKey(Hash(segmentText));

    public void MarkSent(IEnumerable<string> segmentTexts)
    {
        foreach (var text in segmentTexts)
        {
            _seen[Hash(text)] = Interlocked.Increment(ref _tick);
        }

        if (_seen.Count > _capacity)
        {
            EvictOldest();
        }
    }

    public void Clear()
    {
        _seen.Clear();
        _logger.LogInformation("片段快取記錄已清空");
    }

    /// <summary>
    /// 超過容量時丟掉最久沒送過的那些。
    /// 這裡刻意留一成餘裕，避免每次新增都要淘汰一次。
    /// </summary>
    private void EvictOldest()
    {
        var target = _capacity * 9 / 10;
        var toRemove = _seen
            .OrderBy(kv => kv.Value)
            .Take(Math.Max(0, _seen.Count - target))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _seen.TryRemove(key, out _);
        }

        _logger.LogDebug("片段快取記錄淘汰 {N} 筆，剩餘 {Count}", toRemove.Count, _seen.Count);
    }

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }
}
