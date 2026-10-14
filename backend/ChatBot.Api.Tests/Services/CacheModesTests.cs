namespace ChatBot.Api.Tests.Services;

public class CacheModesTests
{
    [Theory]
    [InlineData(null, CacheModes.CacheBlend)]
    [InlineData("", CacheModes.CacheBlend)]
    [InlineData("   ", CacheModes.CacheBlend)]
    [InlineData("default", CacheModes.CacheBlend)]
    [InlineData("cacheblend", CacheModes.CacheBlend)]
    [InlineData("CacheBlend", CacheModes.CacheBlend)]
    [InlineData("  BLEND  ", CacheModes.CacheBlend)]
    [InlineData("lmcache", CacheModes.LmCache)]
    [InlineData("LM", CacheModes.LmCache)]
    [InlineData("prefix", CacheModes.LmCache)]
    [InlineData("none", CacheModes.None)]
    [InlineData("nocache", CacheModes.None)]
    [InlineData("no-cache", CacheModes.None)]
    [InlineData("OFF", CacheModes.None)]
    public void Normalize_把各種寫法對應到正式模式名稱(string? raw, string expected)
    {
        Assert.Equal(expected, CacheModes.Normalize(raw));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("cache blend")]
    [InlineData("123")]
    public void Normalize_無法辨識時退回預設值而不是丟例外(string raw)
    {
        Assert.Equal(CacheModes.Default, CacheModes.Normalize(raw));
    }

    [Fact]
    public void Default_是_CacheBlend()
    {
        Assert.Equal(CacheModes.CacheBlend, CacheModes.Default);
    }

    [Fact]
    public void All_依序列出三種模式()
    {
        Assert.Equal(
            new[] { CacheModes.CacheBlend, CacheModes.LmCache, CacheModes.None },
            CacheModes.All);
    }

    [Theory]
    [InlineData(CacheModes.CacheBlend, "CacheBlend")]
    [InlineData(CacheModes.LmCache, "LMCache")]
    [InlineData(CacheModes.None, "無快取")]
    public void DisplayName_每種模式有各自的顯示名稱(string mode, string expectedPart)
    {
        Assert.Contains(expectedPart, CacheModes.DisplayName(mode));
    }

    [Fact]
    public void DisplayName_未知模式原樣回傳()
    {
        Assert.Equal("whatever", CacheModes.DisplayName("whatever"));
    }
}
