namespace ChatBot.Api.Tests.Services;

public class ChunkOrderStrategyTests
{
    [Theory]
    [InlineData(null, ChunkOrderStrategy.Default)]
    [InlineData("", ChunkOrderStrategy.Default)]
    [InlineData("default", ChunkOrderStrategy.Default)]
    [InlineData("asis", ChunkOrderStrategy.Relevance)]
    [InlineData("AS-IS", ChunkOrderStrategy.Relevance)]
    [InlineData("relevance", ChunkOrderStrategy.Relevance)]
    [InlineData("reverse", ChunkOrderStrategy.Reverse)]
    [InlineData("Shuffle", ChunkOrderStrategy.Shuffle)]
    [InlineData("swap-head", ChunkOrderStrategy.SwapHead)]
    [InlineData("swaphead", ChunkOrderStrategy.SwapHead)]
    [InlineData("swap_head", ChunkOrderStrategy.SwapHead)]
    [InlineData("SWAP-TAIL", ChunkOrderStrategy.SwapTail)]
    [InlineData("swaptail", ChunkOrderStrategy.SwapTail)]
    [InlineData("swap_tail", ChunkOrderStrategy.SwapTail)]
    [InlineData("打錯字", ChunkOrderStrategy.Default)]
    public void Normalize_別名與大小寫都對應到正式名稱(string? raw, string expected)
    {
        Assert.Equal(expected, ChunkOrderStrategy.Normalize(raw));
    }

    [Theory]
    [InlineData(ChunkOrderStrategy.Default, false)]
    [InlineData(ChunkOrderStrategy.Relevance, true)]
    [InlineData(ChunkOrderStrategy.Reverse, true)]
    [InlineData(ChunkOrderStrategy.Shuffle, true)]
    [InlineData(ChunkOrderStrategy.SwapHead, true)]
    [InlineData(ChunkOrderStrategy.SwapTail, true)]
    public void IsExplicit_只有_default_不是驗證用的明確排列(string mode, bool expected)
    {
        Assert.Equal(expected, ChunkOrderStrategy.IsExplicit(mode));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BuildPermutation_少於兩段時不論模式都維持原順序(int count)
    {
        var expected = Enumerable.Range(0, count).ToList();

        foreach (var mode in new[]
                 {
                     ChunkOrderStrategy.Reverse, ChunkOrderStrategy.SwapHead,
                     ChunkOrderStrategy.SwapTail, ChunkOrderStrategy.Shuffle
                 })
        {
            Assert.Equal(expected, ChunkOrderStrategy.BuildPermutation(mode, count, 1));
        }
    }

    [Fact]
    public void BuildPermutation_reverse()
    {
        Assert.Equal(new[] { 4, 3, 2, 1, 0 },
            ChunkOrderStrategy.BuildPermutation(ChunkOrderStrategy.Reverse, 5, 0));
    }

    [Fact]
    public void BuildPermutation_swap_head_只對調最前兩段()
    {
        Assert.Equal(new[] { 1, 0, 2, 3, 4 },
            ChunkOrderStrategy.BuildPermutation(ChunkOrderStrategy.SwapHead, 5, 0));
    }

    [Fact]
    public void BuildPermutation_swap_tail_只對調最後兩段()
    {
        Assert.Equal(new[] { 0, 1, 2, 4, 3 },
            ChunkOrderStrategy.BuildPermutation(ChunkOrderStrategy.SwapTail, 5, 0));
    }

    [Theory]
    [InlineData(ChunkOrderStrategy.Relevance)]
    [InlineData(ChunkOrderStrategy.Default)]
    [InlineData("不認識的模式")]
    public void BuildPermutation_relevance_default_與未知模式不改順序(string mode)
    {
        Assert.Equal(new[] { 0, 1, 2, 3 },
            ChunkOrderStrategy.BuildPermutation(mode, 4, 99));
    }

    // 以下兩個是「黃金值」：由與 C# 逐步等價的 xorshift32 + Fisher-Yates 算出。
    // 驗證報告的數字要能在數個月後重跑出來，所以洗牌結果被鎖死——
    // 這兩個測試失敗，代表有人動了演算法，舊的實驗紀錄就對不上了。
    [Fact]
    public void BuildPermutation_shuffle_預設種子的結果固定()
    {
        Assert.Equal(new[] { 3, 1, 4, 0, 2 },
            ChunkOrderStrategy.BuildPermutation(
                ChunkOrderStrategy.Shuffle, 5, ChunkOrderStrategy.DefaultSeed));
        Assert.Equal(new[] { 1, 2, 0 },
            ChunkOrderStrategy.BuildPermutation(
                ChunkOrderStrategy.Shuffle, 3, ChunkOrderStrategy.DefaultSeed));
    }

    [Fact]
    public void BuildPermutation_shuffle_另一組種子的結果固定()
    {
        Assert.Equal(new[] { 1, 8, 2, 4, 7, 6, 5, 9, 3, 0 },
            ChunkOrderStrategy.BuildPermutation(ChunkOrderStrategy.Shuffle, 10, 12345));
    }

    [Fact]
    public void BuildPermutation_shuffle_種子為0時改用替代狀態而不是卡死()
    {
        // xorshift 不允許全 0 狀態，程式會改用 0x9E3779B9
        Assert.Equal(new[] { 0, 1, 4, 2, 3 },
            ChunkOrderStrategy.BuildPermutation(ChunkOrderStrategy.Shuffle, 5, 0));
    }

    [Fact]
    public void BuildPermutation_shuffle_兩段時一定會換位()
    {
        // 洗出原順序的那次會被丟掉重洗；種子 7 第一次洗出原順序，第二次才換位
        Assert.Equal(new[] { 1, 0 },
            ChunkOrderStrategy.BuildPermutation(ChunkOrderStrategy.Shuffle, 2, 7));
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(8, 42)]
    [InlineData(20, -5)]
    public void BuildPermutation_shuffle_一定是合法排列且不等於原順序(int count, int seed)
    {
        var p = ChunkOrderStrategy.BuildPermutation(ChunkOrderStrategy.Shuffle, count, seed);

        Assert.Equal(Enumerable.Range(0, count), p.OrderBy(x => x));
        Assert.NotEqual(Enumerable.Range(0, count), p);
    }

    [Fact]
    public void PrefixOnlyCeiling_排列與原順序相同時整段都可由前綴供應()
    {
        var ceiling = ChunkOrderStrategy.PrefixOnlyCeiling(
            new[] { 0, 1, 2 }, new[] { 100, 200, 300 }, 450);

        Assert.Equal(450, ceiling);
    }

    [Fact]
    public void PrefixOnlyCeiling_從第一個不同的位置分岔()
    {
        var offsets = new[] { 100, 200, 300, 400, 500 };

        // swap-tail：前三段相同，第四段（index 3）起不同
        Assert.Equal(400, ChunkOrderStrategy.PrefixOnlyCeiling(new[] { 0, 1, 2, 4, 3 }, offsets, 600));
        // swap-head：第一段就不同，只剩固定前綴
        Assert.Equal(100, ChunkOrderStrategy.PrefixOnlyCeiling(new[] { 1, 0, 2, 3, 4 }, offsets, 600));
    }

    [Fact]
    public void PrefixOnlyCeiling_偏移表比排列短時退回總長()
    {
        var ceiling = ChunkOrderStrategy.PrefixOnlyCeiling(
            new[] { 1, 0 }, Array.Empty<int>(), 777);

        Assert.Equal(777, ceiling);
    }
}
