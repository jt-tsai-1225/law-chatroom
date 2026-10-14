namespace ChatBot.Api.Tests.Services;

public class ChunkCacheTrackerTests
{
    private static ChunkCacheTracker Create(int capacity = 4096)
        => new(TestFactory.Options(s => s.ChunkTrackerCapacity = capacity),
               NullLogger<ChunkCacheTracker>.Instance);

    [Fact]
    public void 新的追蹤器是空的()
    {
        var tracker = Create();

        Assert.Equal(0, tracker.Count);
        Assert.False(tracker.IsLikelyCached("第一條"));
    }

    [Fact]
    public void MarkSent_之後該片段被視為可能已快取()
    {
        var tracker = Create();

        tracker.MarkSent(new[] { "第一條", "第二條" });

        Assert.True(tracker.IsLikelyCached("第一條"));
        Assert.True(tracker.IsLikelyCached("第二條"));
        Assert.False(tracker.IsLikelyCached("第三條"));
        Assert.Equal(2, tracker.Count);
    }

    [Fact]
    public void 相同內容重複送出只算一筆()
    {
        var tracker = Create();

        tracker.MarkSent(new[] { "第一條" });
        tracker.MarkSent(new[] { "第一條", "第一條" });

        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void 內容差一個字就是不同的片段()
    {
        var tracker = Create();

        tracker.MarkSent(new[] { "第一條 公司" });

        Assert.False(tracker.IsLikelyCached("第一條 公司 "));
    }

    [Fact]
    public void Clear_清空所有記錄()
    {
        var tracker = Create();
        tracker.MarkSent(new[] { "a", "b", "c" });

        tracker.Clear();

        Assert.Equal(0, tracker.Count);
        Assert.False(tracker.IsLikelyCached("a"));
    }

    [Fact]
    public void 超過容量時淘汰最久沒送過的並留一成餘裕()
    {
        // 容量下限是 16；16 * 9 / 10 = 14，所以 20 筆進來要留下 14 筆
        var tracker = Create(capacity: 16);
        var texts = Enumerable.Range(0, 20).Select(i => $"片段-{i}").ToList();

        tracker.MarkSent(texts);

        Assert.Equal(14, tracker.Count);
        foreach (var oldest in texts.Take(6))
        {
            Assert.False(tracker.IsLikelyCached(oldest));
        }

        foreach (var newest in texts.Skip(6))
        {
            Assert.True(tracker.IsLikelyCached(newest));
        }
    }

    [Fact]
    public void 重新送出的片段會變成最新而不被淘汰()
    {
        var tracker = Create(capacity: 16);
        var texts = Enumerable.Range(0, 16).Select(i => $"片段-{i}").ToList();
        tracker.MarkSent(texts);

        // 最舊的 片段-0 再送一次，它就成了最新
        tracker.MarkSent(new[] { "片段-0" });
        // 再塞 4 筆新的，總數 20 > 16，淘汰 6 筆最舊的
        tracker.MarkSent(new[] { "新-1", "新-2", "新-3", "新-4" });

        Assert.True(tracker.IsLikelyCached("片段-0"));
        Assert.False(tracker.IsLikelyCached("片段-1"));
    }

    [Fact]
    public void 容量設得比下限還小時以_16_為準()
    {
        var tracker = Create(capacity: 1);

        tracker.MarkSent(Enumerable.Range(0, 16).Select(i => $"x{i}"));

        // 剛好 16 筆，沒有超過容量，不應該淘汰任何一筆
        Assert.Equal(16, tracker.Count);
    }
}
