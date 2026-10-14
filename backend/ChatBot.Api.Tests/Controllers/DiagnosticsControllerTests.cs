using ChatBot.Api.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Api.Tests.Controllers;

public class DiagnosticsControllerTests
{
    private readonly Mock<IQdrantService> _qdrant = new();
    private readonly Mock<IPromptBuilder> _promptBuilder = new();
    private readonly Mock<ILLMService> _llm = new();

    /// <summary>BuildAsync 收到的片段（依呼叫順序）。</summary>
    private readonly List<List<RetrievedChunk>> _built = new();

    private static KnowledgeSearchResult Chunk(
        string id, string title, string article, string chapter, string content)
        => new() { Id = id, Title = title, ArticleNumber = article, Chapter = chapter, Content = content };

    private static readonly List<KnowledgeSearchResult> Corpus = new()
    {
        Chunk("c1", "公司法", "第 1 條", "總則", "公司法第一條內文"),
        Chunk("c2", "公司法", "第 2、3 條", "總則", "公司法第二三條內文"),
        Chunk("c3", "民法", "第 1 條", "通則", "民法第一條內文"),
    };

    public DiagnosticsControllerTests()
    {
        _qdrant
            .Setup(q => q.ScrollAllAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(Corpus.ToList());

        _promptBuilder
            .Setup(p => p.BuildAsync(
                It.IsAny<IReadOnlyList<RetrievedChunk>>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ConversationTurn>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<RetrievedChunk>, string, IReadOnlyList<ConversationTurn>?, CancellationToken>(
                (chunks, _, _, _) =>
                {
                    _built.Add(chunks.ToList());
                    return Task.FromResult(new BuiltPrompt
                    {
                        Tokens = new int[500],
                        ChunkCount = chunks.Count,
                        ChunkOffsets = Enumerable.Range(0, chunks.Count).Select(i => 50 + 100 * i).ToArray(),
                        ChunkTokenCounts = Enumerable.Repeat(100, chunks.Count).ToArray(),
                        PrefixTokens = 50,
                    });
                });

        _llm
            .Setup(l => l.GenerateFromTokensAsync(
                It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>(),
                It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<Func<string, Task>?>()))
            .ReturnsAsync(new LLMCompletionResult
            {
                Content = "答案",
                PromptTokens = 500,
                CachedTokens = 250,
                TtftMilliseconds = 40,
                TotalMilliseconds = 90,
            });
    }

    private DiagnosticsController Create()
        => new(_qdrant.Object, _promptBuilder.Object, _llm.Object,
            NullLogger<DiagnosticsController>.Instance);

    // ════════════════════════════════════════════════════════════
    //  GET chunks
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListChunks_回報總數與各法典片段數()
    {
        var result = await Create().ListChunks();

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(3, json.GetProperty("total").GetInt32());
        Assert.Equal(3, json.GetProperty("matched").GetInt32());

        var laws = json.GetProperty("laws").EnumerateArray()
            .ToDictionary(l => l.GetProperty("law").GetString()!, l => l.GetProperty("chunks").GetInt32());
        Assert.Equal(2, laws["公司法"]);
        Assert.Equal(1, laws["民法"]);
    }

    [Fact]
    public async Task ListChunks_依法典篩選_並依條號排序_且拆出個別條號()
    {
        var result = await Create().ListChunks(law: "公司法");

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, json.GetProperty("matched").GetInt32());
        Assert.Equal(3, json.GetProperty("total").GetInt32());

        var chunks = json.GetProperty("chunks").EnumerateArray().ToList();
        Assert.Equal("第 1 條", chunks[0].GetProperty("articleNumber").GetString());
        Assert.Equal("第 2、3 條", chunks[1].GetProperty("articleNumber").GetString());
        Assert.Equal(
            new[] { "2", "3" },
            chunks[1].GetProperty("articles").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("公司法第二三條內文".Length, chunks[1].GetProperty("chars").GetInt32());
    }

    [Theory]
    [InlineData("通則", 1)]      // 比對章節
    [InlineData("總則", 2)]      // 比對章節
    [InlineData("第 2", 1)]      // 比對條號標籤
    [InlineData("民法", 1)]      // 比對法典名
    [InlineData("不存在", 0)]
    public async Task ListChunks_contains_比對條號_章節或法典名(string contains, int expected)
    {
        var result = await Create().ListChunks(contains: contains);

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(expected, json.GetProperty("matched").GetInt32());
    }

    [Fact]
    public async Task ListChunks_沒有條號的片段排在最後而不是丟例外()
    {
        _qdrant
            .Setup(q => q.ScrollAllAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<KnowledgeSearchResult>
            {
                new() { Id = "x", Title = "公司法", ArticleNumber = null, Content = "無條號" },
                new() { Id = "y", Title = "公司法", ArticleNumber = "第 5 條", Content = "有條號" },
                new() { Id = "z", Title = "公司法", ArticleNumber = "附則", Content = "非數字開頭" },
            });

        var result = await Create().ListChunks();

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result.Result).Value);
        var first = json.GetProperty("chunks").EnumerateArray().First();
        Assert.Equal("第 5 條", first.GetProperty("articleNumber").GetString());
    }

    // ════════════════════════════════════════════════════════════
    //  POST probe：輸入驗證
    // ════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Probe_問題為空回_400(string question)
    {
        var result = await Create().Probe(new ProbeRequest { Question = question, Articles = { "1" } });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _llm.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Probe_沒有指定條號回_400()
    {
        var result = await Create().Probe(new ProbeRequest { Question = "Q" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _llm.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Probe_找不到指定法典時回_400_並列出可用法典()
    {
        var result = await Create().Probe(
            new ProbeRequest { Question = "Q", Articles = { "1" }, Law = "刑法" });

        var json = TestFactory.ToJson(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains("刑法", json.GetProperty("error").GetString());
        Assert.Equal(
            new[] { "公司法", "民法" }.OrderBy(x => x),
            json.GetProperty("availableLaws").EnumerateArray().Select(x => x.GetString()).OrderBy(x => x));
        _llm.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Probe_條號跨法典重複又沒指定法典_寧可報錯也不任選一個()
    {
        // 第 1 條在公司法與民法都有
        var result = await Create().Probe(new ProbeRequest { Question = "Q", Articles = { "1" } });

        var json = TestFactory.ToJson(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Equal("1", json.GetProperty("article").GetString());
        Assert.Equal(2, json.GetProperty("candidates").GetArrayLength());
        _llm.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Probe_指定的條號全都找不到回_404()
    {
        var result = await Create().Probe(
            new ProbeRequest { Question = "Q", Articles = { "999" }, Law = "公司法" });

        var json = TestFactory.ToJson(Assert.IsType<NotFoundObjectResult>(result.Result).Value);
        Assert.Equal("999", json.GetProperty("notFound")[0].GetString());
        _llm.VerifyNoOtherCalls();
    }

    // ════════════════════════════════════════════════════════════
    //  POST probe：挑片段與送出
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Probe_順序完全照請求給的_不去重不重排()
    {
        var result = await Create().Probe(new ProbeRequest
        {
            Question = "Q",
            Articles = { "2", "1" },
            Law = "公司法",
        });

        var chunks = Assert.Single(_built);
        Assert.Equal(new[] { "第 2、3 條", "第 1 條" }, chunks.Select(c => c.ArticleNumber));

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(new[] { "第 2、3 條", "第 1 條" },
            json.GetProperty("order").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(0, json.GetProperty("notFound").GetArrayLength());
    }

    [Fact]
    public async Task Probe_落在同一個片段的兩個條號只取一次()
    {
        var result = await Create().Probe(new ProbeRequest
        {
            Question = "Q",
            Articles = { "2", "3" },       // 都在「第 2、3 條」這個片段
            Law = "公司法",
        });

        Assert.Single(Assert.Single(_built));
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Probe_部分條號找不到時照常送出_並回報找不到的()
    {
        var result = await Create().Probe(new ProbeRequest
        {
            Question = "Q",
            Articles = { " 1 ", "99" },    // 前後空白會被修掉
            Law = "公司法",
        });

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("99", json.GetProperty("notFound")[0].GetString());
        Assert.Single(Assert.Single(_built));
    }

    [Fact]
    public async Task Probe_沒指定法典但條號只在一部法典時可以用()
    {
        var result = await Create().Probe(new ProbeRequest { Question = "Q", Articles = { "2" } });

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("公司法", json.GetProperty("laws")[0].GetString());
    }

    [Fact]
    public async Task Probe_回應帶引擎的量測值與各片段的位置()
    {
        var result = await Create().Probe(new ProbeRequest
        {
            Question = "公司重整是什麼",
            Articles = { "1", "2" },
            Law = "公司法",
        });

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("公司重整是什麼", json.GetProperty("question").GetString());
        Assert.Equal(500, json.GetProperty("promptTokens").GetInt32());
        Assert.Equal(250, json.GetProperty("cachedTokens").GetInt32());
        Assert.Equal(50, json.GetProperty("cacheHitRate").GetDouble());
        Assert.Equal(40, json.GetProperty("ttftMilliseconds").GetInt64());
        Assert.Equal(90, json.GetProperty("totalMilliseconds").GetInt64());
        Assert.Equal(500, json.GetProperty("builtTokens").GetInt32());
        Assert.Equal(50, json.GetProperty("prefixTokens").GetInt32());
        Assert.Equal("答案", json.GetProperty("reply").GetString());

        var segments = json.GetProperty("segments").EnumerateArray().ToList();
        Assert.Equal(2, segments.Count);
        Assert.Equal(1, segments[0].GetProperty("position").GetInt32());
        Assert.Equal(50, segments[0].GetProperty("offset").GetInt32());
        Assert.Equal(150, segments[1].GetProperty("offset").GetInt32());
        Assert.Equal(100, segments[1].GetProperty("tokens").GetInt32());
        Assert.Equal("公司法", segments[0].GetProperty("law").GetString());
    }

    [Fact]
    public async Task Probe_組好的_token_原樣交給引擎()
    {
        await Create().Probe(new ProbeRequest { Question = "Q", Articles = { "1" }, Law = "公司法" });

        _llm.Verify(l => l.GenerateFromTokensAsync(
            It.Is<IReadOnlyList<int>>(t => t.Count == 500),
            It.IsAny<CancellationToken>(), It.IsAny<int?>(), It.IsAny<string?>(),
            It.IsAny<Func<string, Task>?>()), Times.Once);
    }
}
