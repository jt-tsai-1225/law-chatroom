using System.Text;

namespace ChatBot.Api.Tests.Services;

public class LMCacheServiceTests
{
    private static LMCacheService Create(
        bool enabled = true, int maxEntries = 1000, int ttlMinutes = 60)
        => new(
            TestFactory.Options(s =>
            {
                s.LmcacheEnabled = enabled;
                s.LmcacheMaxEntries = maxEntries;
                s.LmcacheDefaultTtlMinutes = ttlMinutes;
            }),
            NullLogger<LMCacheService>.Instance);

    // ── 啟用與否 ────────────────────────────────────────────────

    [Fact]
    public void 沒有傳入設定時預設啟用()
    {
        var service = new LMCacheService();

        Assert.True(service.IsEnabled);
    }

    [Fact]
    public void 設定關閉時不啟用()
    {
        Assert.False(Create(enabled: false).IsEnabled);
    }

    [Fact]
    public void 最大項目數為零時等於不啟用()
    {
        Assert.False(Create(enabled: true, maxEntries: 0).IsEnabled);
    }

    [Fact]
    public void 未啟用時所有操作都是空操作且_Get_算一次未命中()
    {
        var service = Create(enabled: false);

        service.Set("k", "v");

        Assert.Null(service.Get("k"));
        Assert.False(service.Contains("k"));
        service.Remove("k");
        service.Clear();
        Assert.Equal(0, service.HitCount);
        Assert.Equal(1, service.MissCount);
        Assert.Equal(0, service.GetStats().TotalEntries);
    }

    // ── 基本存取 ────────────────────────────────────────────────

    [Fact]
    public void Set_之後_Get_命中並累計次數()
    {
        var service = Create();

        service.Set("k", "回答");

        Assert.Equal("回答", service.Get("k"));
        Assert.Equal(1, service.HitCount);
        Assert.Equal(0, service.MissCount);
    }

    [Fact]
    public void Get_不存在的鍵算未命中()
    {
        var service = Create();

        Assert.Null(service.Get("nope"));
        Assert.Equal(1, service.MissCount);
    }

    [Theory]
    [InlineData("", "value")]
    [InlineData("key", "")]
    public void Set_空鍵或空內容不會被存起來(string key, string value)
    {
        var service = Create();

        service.Set(key, value);

        Assert.Equal(0, service.GetStats().TotalEntries);
    }

    [Fact]
    public void Set_同一個鍵再存一次會覆蓋()
    {
        var service = Create();

        service.Set("k", "舊");
        service.Set("k", "新");

        Assert.Equal("新", service.Get("k"));
        Assert.Equal(1, service.GetStats().TotalEntries);
    }

    [Fact]
    public void Contains_與_Remove()
    {
        var service = Create();
        service.Set("k", "v");

        Assert.True(service.Contains("k"));

        service.Remove("k");

        Assert.False(service.Contains("k"));
        Assert.Null(service.Get("k"));
    }

    [Fact]
    public void Clear_清空項目並歸零統計()
    {
        var service = Create();
        service.Set("a", "1");
        service.Get("a");
        service.Get("missing");

        service.Clear();

        var stats = service.GetStats();
        Assert.Equal(0, stats.TotalEntries);
        Assert.Equal(0, stats.HitCount);
        Assert.Equal(0, stats.MissCount);
    }

    // ── 過期與淘汰 ──────────────────────────────────────────────

    [Fact]
    public void 超過_TTL_的項目讀取時被移除並算未命中()
    {
        // TTL 設成負數 = 一建立就已過期，免得測試靠睡眠等時間
        var service = Create(ttlMinutes: -1);
        service.Set("k", "v");

        Assert.Null(service.Get("k"));
        Assert.False(service.Contains("k"));
        Assert.Equal(1, service.MissCount);
    }

    [Fact]
    public void 項目滿了之後淘汰最舊的()
    {
        var service = Create(maxEntries: 2);

        service.Set("a", "1");
        Thread.Sleep(20);   // 讓建立時間有明確先後
        service.Set("b", "2");
        Thread.Sleep(20);
        service.Set("c", "3");

        Assert.False(service.Contains("a"));
        Assert.True(service.Contains("b"));
        Assert.True(service.Contains("c"));
    }

    // ── 統計 ────────────────────────────────────────────────────

    [Fact]
    public void GetStats_回報項目數與位元組數()
    {
        var service = Create();
        service.Set("a", "abc");        // 3 bytes
        service.Set("b", "你好");        // 6 bytes（UTF-8 每個中文字 3 bytes）

        var stats = service.GetStats();

        Assert.Equal(2, stats.TotalEntries);
        Assert.Equal(3 + Encoding.UTF8.GetByteCount("你好"), stats.TotalSizeBytes);
    }

    [Fact]
    public void CacheStats_命中率以百分比計()
    {
        Assert.Equal(0, new CacheStats().HitRate);
        Assert.Equal(75, new CacheStats { HitCount = 3, MissCount = 1 }.HitRate);
    }

    // ── 快取鍵 ──────────────────────────────────────────────────

    [Fact]
    public void GenerateCacheKey_是固定的小寫十六進位_SHA256()
    {
        // sha256("sys|user")
        Assert.Equal(
            "01056f9699a102de640131fd775dca4a214c9b6802278a1b372edf394e87e30f",
            LMCacheService.GenerateCacheKey("sys", "user"));
    }

    [Fact]
    public void GenerateCacheKey_系統提示與使用者提問都影響鍵()
    {
        var baseKey = LMCacheService.GenerateCacheKey("sys", "user");

        Assert.NotEqual(baseKey, LMCacheService.GenerateCacheKey("sys2", "user"));
        Assert.NotEqual(baseKey, LMCacheService.GenerateCacheKey("sys", "user2"));
        Assert.Equal(baseKey, LMCacheService.GenerateCacheKey("sys", "user"));
    }

    // ── 設定與資料類別的預設值 ──────────────────────────────────

    [Fact]
    public void LMCacheSettings_預設值()
    {
        var settings = new LMCacheSettings();

        Assert.Equal("LMCache", LMCacheSettings.SectionName);
        Assert.True(settings.Enabled);
        Assert.Equal(1000, settings.MaxEntries);
        Assert.Equal(60, settings.DefaultTtlMinutes);
        Assert.Equal("memory", settings.StorageType);
        Assert.Null(settings.StoragePath);
    }

    [Fact]
    public void CacheEntry_預設值()
    {
        var entry = new CacheEntry();

        Assert.Equal("", entry.Key);
        Assert.Equal("", entry.Value);
        Assert.Equal(0, entry.AccessCount);
        Assert.Null(entry.Metadata);
        Assert.Null(entry.Hash);
    }
}
