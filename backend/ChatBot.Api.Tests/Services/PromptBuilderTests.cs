namespace ChatBot.Api.Tests.Services;

public class PromptBuilderTests
{
    private static readonly int[] Separator = { 422, 422 };

    private static (PromptBuilder Builder, FakeTokenizer Tokenizer) Create(
        Action<RAGSettings>? configure = null)
    {
        var tokenizer = new FakeTokenizer();
        tokenizer.Overrides["# #"] = Separator;

        var options = TestFactory.Options(s =>
        {
            s.SystemPrompt = "SYS";
            s.BlendSeparator = "# #";
            s.ChunkWarnMinTokens = 0;       // 預設會對短片段警告，測試用的片段都很短
            configure?.Invoke(s);
        });

        return (new PromptBuilder(tokenizer, options, NullLogger<PromptBuilder>.Instance), tokenizer);
    }

    private static RetrievedChunk Chunk(string content, string article = "")
        => new() { Content = content, ArticleNumber = article };

    // ── 結構 ────────────────────────────────────────────────────

    [Fact]
    public async Task 依規定的結構組出_token_陣列與偏移()
    {
        var (builder, _) = Create();

        var prompt = await builder.BuildAsync(new[] { Chunk("AB"), Chunk("CD") }, "Q");

        // BOS+[INST] | 系統 | 分隔符 片段1 | 分隔符 片段2 | 分隔符 問題 | [/INST]
        var expected = new[]
        {
            1, 733, 16289, 28793,
            83, 89, 83,
            422, 422, 65, 66,
            422, 422, 67, 68,
            422, 422, 81,
            733, 28748, 16289, 28793
        };
        Assert.Equal(expected, prompt.Tokens);
        Assert.Equal(2, prompt.ChunkCount);
        Assert.Equal(new[] { 9, 13 }, prompt.ChunkOffsets);
        Assert.Equal(new[] { 2, 2 }, prompt.ChunkTokenCounts);
        Assert.Equal(7, prompt.PrefixTokens);
        Assert.Equal(0, prompt.HistoryMessagesUsed);
    }

    [Fact]
    public async Task 沒有任何片段時仍是合法的_prompt()
    {
        var (builder, _) = Create();

        var prompt = await builder.BuildAsync(Array.Empty<RetrievedChunk>(), "Q");

        Assert.Equal(0, prompt.ChunkCount);
        Assert.Empty(prompt.ChunkOffsets);
        Assert.Equal(new[] { 1, 733, 16289, 28793, 83, 89, 83, 422, 422, 81, 733, 28748, 16289, 28793 },
            prompt.Tokens);
    }

    [Fact]
    public async Task 分隔符與系統提示詞走快取版_片段內容也走快取版_問題不走()
    {
        var (builder, tokenizer) = Create();

        await builder.BuildAsync(new[] { Chunk("AB") }, "Q");

        Assert.Equal(new[] { "# #", "SYS", "AB" }, tokenizer.CachedCalls);
        Assert.Equal(new[] { "Q" }, tokenizer.Calls);   // 問題每次都不同，快取沒有意義
    }

    [Fact]
    public async Task 片段已帶_token_時直接使用而不再呼叫_tokenizer()
    {
        var (builder, tokenizer) = Create();
        var preTokenized = new RetrievedChunk { Content = "AB", Tokens = new[] { 7, 8, 9 } };

        var prompt = await builder.BuildAsync(new[] { preTokenized }, "Q");

        Assert.Equal(new[] { 3 }, prompt.ChunkTokenCounts);
        Assert.DoesNotContain("AB", tokenizer.CachedCalls);
    }

    [Fact]
    public async Task 分隔符_tokenize_後為空時丟出明確的例外()
    {
        var (builder, tokenizer) = Create();
        tokenizer.Overrides["# #"] = Array.Empty<int>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => builder.BuildAsync(new[] { Chunk("AB") }, "Q"));

        Assert.Contains("# #", ex.Message);
    }

    [Fact]
    public async Task 過短的片段只警告不拒絕()
    {
        var (builder, _) = Create(s => s.ChunkWarnMinTokens = 256);

        var prompt = await builder.BuildAsync(new[] { Chunk("AB") }, "Q");

        Assert.Equal(new[] { 2 }, prompt.ChunkTokenCounts);
    }

    // ── 對話歷史 ────────────────────────────────────────────────

    [Fact]
    public async Task 歷史放在片段之後的尾段_與問題同一段()
    {
        var (builder, tokenizer) = Create();
        var history = new[]
        {
            new ConversationTurn("user", "先前的問題"),
            new ConversationTurn("assistant", "先前的回答")
        };

        var prompt = await builder.BuildAsync(new[] { Chunk("AB") }, "本次的問題", history);

        var tail = tokenizer.Calls.Last();
        Assert.Contains("使用者：先前的問題", tail);
        Assert.Contains("助理：先前的回答", tail);
        Assert.EndsWith("本次問題：本次的問題", tail);
        Assert.Equal(2, prompt.HistoryMessagesUsed);
        // 片段的位置不因歷史而改變
        Assert.Equal(new[] { 9 }, prompt.ChunkOffsets);
    }

    [Fact]
    public async Task 內容空白的歷史訊息被略過()
    {
        var (builder, tokenizer) = Create();
        var history = new[]
        {
            new ConversationTurn("user", "   "),
            new ConversationTurn("assistant", ""),
        };

        var prompt = await builder.BuildAsync(Array.Empty<RetrievedChunk>(), "Q", history);

        Assert.Equal(0, prompt.HistoryMessagesUsed);
        Assert.Equal("Q", tokenizer.Calls.Last());
    }

    [Fact]
    public async Task 歷史超過則數上限時只留最近的()
    {
        var (builder, tokenizer) = Create(s => s.MaxHistoryMessages = 2);
        var history = new[]
        {
            new ConversationTurn("user", "第一則"),
            new ConversationTurn("assistant", "第二則"),
            new ConversationTurn("user", "第三則"),
            new ConversationTurn("assistant", "第四則"),
        };

        var prompt = await builder.BuildAsync(Array.Empty<RetrievedChunk>(), "Q", history);

        var tail = tokenizer.Calls.Last();
        Assert.Equal(2, prompt.HistoryMessagesUsed);
        Assert.DoesNotContain("第一則", tail);
        Assert.DoesNotContain("第二則", tail);
        Assert.Contains("第三則", tail);
        Assert.Contains("第四則", tail);
    }

    [Fact]
    public async Task 總長超過上限時從最舊的歷史開始丟()
    {
        // 假 tokenizer 一個字元一個 token。固定部分 = 4 + 3 = 7，分隔符 2，
        // 預算 = 50 - 4 = 46，所以尾段最多 46 - 7 - 2 = 37 個字元。
        // 兩則歷史的尾段是 39（LF）或 43（CRLF）字元，放不下；
        // 丟掉最舊的之後只剩 33 或 36 字元，放得下。兩種換行都落在同一側。
        var (builder, tokenizer) = Create(s => s.MaxPromptTokens = 50);
        var history = new[]
        {
            new ConversationTurn("user", "a"),
            new ConversationTurn("assistant", "b"),
        };

        var prompt = await builder.BuildAsync(Array.Empty<RetrievedChunk>(), "Q", history);

        var tail = tokenizer.Calls.Last();
        Assert.Equal(1, prompt.HistoryMessagesUsed);
        Assert.DoesNotContain("使用者：a", tail);   // 最舊的那則被丟掉
        Assert.Contains("助理：b", tail);
    }

    [Fact]
    public async Task 歷史全部丟掉也放不下時只剩問題本身()
    {
        var (builder, tokenizer) = Create(s => s.MaxPromptTokens = 20);
        var history = new[]
        {
            new ConversationTurn("user", "a"),
            new ConversationTurn("assistant", "b"),
            new ConversationTurn("user", "c"),
        };

        var prompt = await builder.BuildAsync(Array.Empty<RetrievedChunk>(), "Q", history);

        Assert.Equal(0, prompt.HistoryMessagesUsed);
        Assert.Equal("Q", tokenizer.Calls.Last());
    }

    // ── RetrievedChunk ──────────────────────────────────────────

    [Fact]
    public void RetrievedChunk_片段文字就是內容_不加任何標記()
    {
        // 這是快取鍵的來源：多一個空白或「[資料 1]」這種隨位置改變的標記都會讓快取永遠不中
        var chunk = new RetrievedChunk { ArticleNumber = "第 1 條", Content = "公司為法人。" };

        Assert.Equal("公司為法人。", chunk.ComposeSegmentText());
    }

    [Fact]
    public void RetrievedChunk_沒有條號時標示為無條號()
    {
        Assert.Equal("(無條號)", new RetrievedChunk { Content = "x" }.Label);
        Assert.Equal("第 2 條", new RetrievedChunk { ArticleNumber = "第 2 條", Content = "x" }.Label);
    }
}
