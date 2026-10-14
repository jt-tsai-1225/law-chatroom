namespace ChatBot.Api.Tests.Services;

/// <summary>
/// ChatBotService 的流程測試：外部相依（Qdrant、embedding、prompt 組裝、vLLM、資料庫）全部以替身取代，
/// 只驗證「這支服務自己決定的事」——檢索合併、去重、排序、模式正規化、
/// 命中率欄位、聊天室寫入與失敗時的補救。
/// </summary>
public class ChatBotServiceTests
{
    private readonly Mock<IQdrantService> _qdrant = new();
    private readonly Mock<IEmbeddingService> _embedding = new();
    private readonly Mock<IPromptBuilder> _promptBuilder = new();
    private readonly Mock<ILLMService> _llm = new();
    private readonly Mock<IChunkCacheTracker> _tracker = new();
    private readonly Mock<IConversationStore> _conversations = new();

    /// <summary>每次 BuildAsync 收到的片段（依呼叫順序）。</summary>
    private readonly List<List<RetrievedChunk>> _builtChunks = new();

    /// <summary>每次 BuildAsync 收到的對話歷史。</summary>
    private readonly List<IReadOnlyList<ConversationTurn>?> _builtHistory = new();

    /// <summary>寫進聊天室的訊息。</summary>
    private readonly List<ConversationMessage> _appended = new();

    private static readonly Guid RoomId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public ChatBotServiceTests()
    {
        _embedding
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { 0.1f });

        _promptBuilder
            .Setup(p => p.BuildAsync(
                It.IsAny<IReadOnlyList<RetrievedChunk>>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ConversationTurn>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<RetrievedChunk>, string, IReadOnlyList<ConversationTurn>?, CancellationToken>(
                (chunks, _, history, _) =>
                {
                    _builtChunks.Add(chunks.ToList());
                    _builtHistory.Add(history);

                    // 每段 100 token：第 i 段從 100 + 100*i 開始，總長 1000
                    return Task.FromResult(new BuiltPrompt
                    {
                        Tokens = new int[1000],
                        ChunkCount = chunks.Count,
                        ChunkOffsets = Enumerable.Range(0, chunks.Count).Select(i => 100 + 100 * i).ToArray(),
                        ChunkTokenCounts = Enumerable.Repeat(100, chunks.Count).ToArray(),
                        PrefixTokens = 100,
                        HistoryMessagesUsed = history?.Count ?? 0,
                    });
                });

        LlmReturns(DefaultResult());

        _conversations.SetupGet(c => c.IsConfigured).Returns(true);
        _conversations
            .Setup(c => c.RecentMessagesAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _conversations
            .Setup(c => c.AppendAsync(It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationMessage, CancellationToken>((m, _) => _appended.Add(m))
            .ReturnsAsync(1L);

        SearchReturns();   // 預設查不到任何東西，各測試自行指定
    }

    // ── 測試用的小工具 ─────────────────────────────────────────

    private ChatBotService Create(Action<RAGSettings>? configure = null)
        => new(
            _qdrant.Object,
            _embedding.Object,
            _promptBuilder.Object,
            _llm.Object,
            _tracker.Object,
            _conversations.Object,
            TestFactory.Options(configure),
            NullLogger<ChatBotService>.Instance);

    private static LLMCompletionResult DefaultResult(string content = "答案") => new()
    {
        Content = content,
        TtftMilliseconds = 50,
        TokenCount = 10,
        TokensPerSecond = 20,
        PromptTokens = 1000,
        CachedTokens = 800,
    };

    private void LlmReturns(LLMCompletionResult result)
        => _llm
            .Setup(l => l.GenerateFromTokensAsync(
                It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>(),
                It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<Func<string, Task>?>()))
            .ReturnsAsync(result);

    private void LlmThrows(Exception exception)
        => _llm
            .Setup(l => l.GenerateFromTokensAsync(
                It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>(),
                It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<Func<string, Task>?>()))
            .ThrowsAsync(exception);

    private static (KnowledgeSearchResult Result, double Score) Hit(
        string id, string article, string content, double score)
        => (new KnowledgeSearchResult { Id = id, ArticleNumber = article, Content = content, Score = score }, score);

    /// <summary>不論查詢向量是什麼，都回傳這些結果。</summary>
    private void SearchReturns(params (KnowledgeSearchResult Result, double Score)[] hits)
        => _qdrant
            .Setup(q => q.SearchSimilarAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>()))
            .ReturnsAsync(hits.ToList());

    /// <summary>只有查詢向量的第一個分量等於 marker 時才回傳這些結果。</summary>
    private void SearchReturnsFor(float marker, params (KnowledgeSearchResult Result, double Score)[] hits)
        => _qdrant
            .Setup(q => q.SearchSimilarAsync(
                It.IsAny<string>(), It.Is<float[]>(v => v.Length > 0 && v[0] == marker), It.IsAny<int>()))
            .ReturnsAsync(hits.ToList());

    private void ThreeHits()
        => SearchReturns(
            Hit("a", "第 1 條", "內文A", 0.9),
            Hit("b", "第 2 條", "內文B", 0.8),
            Hit("c", "第 3 條", "內文C", 0.7));

    private static ChatRequest Ask(string message = "問題", Guid? room = null, string? cacheMode = null,
        string? order = null, int? seed = null)
        => new()
        {
            Message = message,
            ConversationId = room,
            CacheMode = cacheMode,
            ChunkOrder = order,
            ChunkOrderSeed = seed,
        };

    private static List<string> Contents(IEnumerable<RetrievedChunk> chunks)
        => chunks.Select(c => c.Content).ToList();

    private static ConversationMessage Stored(string role, string content)
        => new() { ConversationId = RoomId, Role = role, Content = content };

    // ════════════════════════════════════════════════════════════
    //  主流程與回應欄位
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task 正常流程_回傳帶標題與免責聲明的答案_並保留原始輸出()
    {
        ThreeHits();

        var response = await Create().GetReplyAsync(Ask());

        Assert.Equal("答案", response.RawReply);
        Assert.StartsWith("📜 **法律諮詢回覆：**", response.Reply);
        Assert.Contains("答案", response.Reply);
        Assert.Contains("免責聲明", response.Reply);
        _qdrant.Verify(q => q.InitializeCollectionAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task 量測欄位與命中率直接來自推論引擎的回報()
    {
        ThreeHits();

        var response = await Create().GetReplyAsync(Ask());

        Assert.Equal(50, response.TtftMilliseconds);
        Assert.Equal(10, response.TokenCount);
        Assert.Equal(20, response.TokensPerSecond);
        Assert.Equal(1000, response.PromptTokens);
        Assert.Equal(800, response.CachedTokens);
        Assert.Equal(80, response.CacheHitRate);
        Assert.False(response.FromCache);
    }

    [Fact]
    public async Task 檢索到的片段依序放進_prompt_並回報標籤()
    {
        ThreeHits();

        var response = await Create().GetReplyAsync(Ask());

        Assert.Equal(new[] { "內文A", "內文B", "內文C" }, Contents(Assert.Single(_builtChunks)));
        Assert.Equal(new[] { "第 1 條", "第 2 條", "第 3 條" }, response.RetrievedArticles);
        Assert.Equal(new[] { 0, 1, 2 }, response.ChunkPermutation);
        Assert.False(response.ReorderedForCache);
        Assert.Equal(ChunkOrderStrategy.Default, response.ChunkOrderApplied);
        Assert.Null(response.ChunkOrderSeedUsed);
    }

    [Fact]
    public async Task 檢索以_topK_設定取片段()
    {
        ThreeHits();

        await Create(s => s.RetrievalTopK = 3).GetReplyAsync(Ask());

        _qdrant.Verify(q => q.SearchSimilarAsync(
            "legal_documents", It.IsAny<float[]>(), 3), Times.Once);
    }

    [Fact]
    public async Task 成功之後才把片段登記為已送出()
    {
        ThreeHits();

        await Create().GetReplyAsync(Ask());

        _tracker.Verify(t => t.MarkSent(
            It.Is<IEnumerable<string>>(s => s.SequenceEqual(new[] { "內文A", "內文B", "內文C" }))), Times.Once);
    }

    [Fact]
    public async Task 引擎失敗時片段不會被登記為已送出()
    {
        ThreeHits();
        LlmThrows(new InvalidOperationException("引擎掛了"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().GetReplyAsync(Ask()));

        _tracker.Verify(t => t.MarkSent(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task 引擎回空內容視為失敗()
    {
        ThreeHits();
        LlmReturns(DefaultResult(content: "   "));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Create().GetReplyAsync(Ask()));

        Assert.Contains("空內容", ex.Message);
    }

    [Fact]
    public async Task 逐段文字回呼原樣交給引擎()
    {
        ThreeHits();
        _llm
            .Setup(l => l.GenerateFromTokensAsync(
                It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>(),
                It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<Func<string, Task>?>()))
            .Returns<IReadOnlyList<int>, CancellationToken, int?, string?, Func<string, Task>?>(
                async (_, _, _, _, onDelta) =>
                {
                    await onDelta!("甲");
                    await onDelta("乙");
                    return DefaultResult();
                });
        var deltas = new List<string>();

        await Create().GetReplyAsync(Ask(), default, d =>
        {
            deltas.Add(d);
            return Task.CompletedTask;
        });

        Assert.Equal(new[] { "甲", "乙" }, deltas);
    }

    // ════════════════════════════════════════════════════════════
    //  查無條文
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task 查無條文時直接回覆_不呼叫引擎()
    {
        var response = await Create().GetReplyAsync(Ask());

        Assert.Contains("查無相關條文", response.Reply);
        Assert.Equal("查無相關條文，無法回答這個問題。", response.RawReply);
        Assert.Empty(response.RetrievedArticles);
        _llm.VerifyNoOtherCalls();
        _promptBuilder.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 查無條文的這一輪也會寫進聊天室()
    {
        var response = await Create().GetReplyAsync(Ask("沒人知道的問題", RoomId));

        Assert.True(response.Persisted);
        Assert.Equal(new[] { "user", "assistant" }, _appended.Select(m => m.Role));
        Assert.Equal("沒人知道的問題", _appended[0].Content);
        Assert.Equal("查無相關條文，無法回答這個問題。", _appended[1].Content);
    }

    // ════════════════════════════════════════════════════════════
    //  去重與排序
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task 內容完全相同的片段只留第一個()
    {
        SearchReturns(
            Hit("a", "第 287 條", "同樣的內文", 0.9),
            Hit("b", "第 287 條", "同樣的內文", 0.85),
            Hit("c", "第 288 條", "另一條", 0.8));

        var response = await Create().GetReplyAsync(Ask());

        Assert.Equal(new[] { "同樣的內文", "另一條" }, Contents(Assert.Single(_builtChunks)));
        Assert.Equal(2, response.RetrievedArticles.Count);
    }

    [Fact]
    public async Task 條號相同但內容不同的片段不合併()
    {
        SearchReturns(
            Hit("a", "第 287 條", "版本一", 0.9),
            Hit("b", "第 287 條", "版本二", 0.8));

        await Create().GetReplyAsync(Ask());

        Assert.Equal(2, Assert.Single(_builtChunks).Count);
    }

    [Fact]
    public async Task 可能已快取的片段排到前面_排列表反映新順序()
    {
        ThreeHits();
        _tracker.Setup(t => t.IsLikelyCached("內文B")).Returns(true);

        var response = await Create().GetReplyAsync(Ask());

        Assert.Equal(new[] { "內文B", "內文A", "內文C" }, Contents(Assert.Single(_builtChunks)));
        Assert.True(response.ReorderedForCache);
        Assert.Equal(new[] { 1, 0, 2 }, response.ChunkPermutation);
        Assert.Equal(new[] { "第 2 條", "第 1 條", "第 3 條" }, response.RetrievedArticles);
    }

    [Fact]
    public async Task 兩邊各自維持原本的相關度順序()
    {
        ThreeHits();
        _tracker.Setup(t => t.IsLikelyCached("內文C")).Returns(true);
        _tracker.Setup(t => t.IsLikelyCached("內文A")).Returns(true);

        await Create().GetReplyAsync(Ask());

        Assert.Equal(new[] { "內文A", "內文C", "內文B" }, Contents(Assert.Single(_builtChunks)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 全部已快取或全部未快取時維持原順序(bool allCached)
    {
        ThreeHits();
        _tracker.Setup(t => t.IsLikelyCached(It.IsAny<string>())).Returns(allCached);

        var response = await Create().GetReplyAsync(Ask());

        Assert.False(response.ReorderedForCache);
        Assert.Equal(new[] { "內文A", "內文B", "內文C" }, Contents(Assert.Single(_builtChunks)));
    }

    [Fact]
    public async Task 只有一個片段時不必重排()
    {
        SearchReturns(Hit("a", "第 1 條", "內文A", 0.9));

        var response = await Create().GetReplyAsync(Ask());

        Assert.False(response.ReorderedForCache);
        _tracker.Verify(t => t.IsLikelyCached(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task 關閉依快取重排時不查追蹤器()
    {
        ThreeHits();
        _tracker.Setup(t => t.IsLikelyCached(It.IsAny<string>())).Returns(true);

        var response = await Create(s => s.ReorderByCacheStatus = false).GetReplyAsync(Ask());

        Assert.False(response.ReorderedForCache);
        _tracker.Verify(t => t.IsLikelyCached(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task 明確指定排列時不查追蹤器_排列表與前綴上限正確()
    {
        ThreeHits();
        _tracker.Setup(t => t.IsLikelyCached(It.IsAny<string>())).Returns(true);

        var response = await Create().GetReplyAsync(Ask(order: "reverse"));

        Assert.Equal(new[] { "內文C", "內文B", "內文A" }, Contents(Assert.Single(_builtChunks)));
        Assert.Equal(ChunkOrderStrategy.Reverse, response.ChunkOrderApplied);
        Assert.Equal(new[] { 2, 1, 0 }, response.ChunkPermutation);
        Assert.False(response.ReorderedForCache);
        Assert.Null(response.ChunkOrderSeedUsed);
        _tracker.Verify(t => t.IsLikelyCached(It.IsAny<string>()), Times.Never);

        // 第 0 項就與原順序不同 → 前綴只能共用到第一段開始之前（偏移 100）
        Assert.Equal(100, response.PrefixOnlyCeilingTokens);
        Assert.Equal(8, response.CachedOverPrefixCeiling);   // 800 / 100
    }

    [Fact]
    public async Task 維持相關度順序時前綴上限等於整段_prompt()
    {
        ThreeHits();

        var response = await Create().GetReplyAsync(Ask(order: "relevance"));

        Assert.Equal(1000, response.PrefixOnlyCeilingTokens);
        Assert.Equal(0.8, response.CachedOverPrefixCeiling);   // 800 / 1000
    }

    [Fact]
    public async Task 舊名_asis_回報為正式名稱_relevance()
    {
        ThreeHits();

        var response = await Create().GetReplyAsync(Ask(order: "asis"));

        Assert.Equal(ChunkOrderStrategy.Relevance, response.ChunkOrderApplied);
    }

    [Fact]
    public async Task 只對調最後兩段時前綴上限停在倒數第二段開頭()
    {
        ThreeHits();

        var response = await Create().GetReplyAsync(Ask(order: "swap-tail"));

        Assert.Equal(new[] { 0, 2, 1 }, response.ChunkPermutation);
        Assert.Equal(200, response.PrefixOnlyCeilingTokens);   // k = 1 → ChunkOffsets[1]
    }

    [Fact]
    public async Task 無法辨識的排列模式退回預設_不使請求失敗()
    {
        ThreeHits();

        var response = await Create().GetReplyAsync(Ask(order: "亂打的"));

        Assert.Equal(ChunkOrderStrategy.Default, response.ChunkOrderApplied);
    }

    [Fact]
    public async Task shuffle_使用預設種子時排列可重現()
    {
        ThreeHits();

        var response = await Create().GetReplyAsync(Ask(order: "shuffle"));

        // 固定規格：shuffle(3, 20260930) = [1,2,0]
        Assert.Equal(new[] { 1, 2, 0 }, response.ChunkPermutation);
        Assert.Equal(new[] { "內文B", "內文C", "內文A" }, Contents(Assert.Single(_builtChunks)));
        Assert.Equal(ChunkOrderStrategy.DefaultSeed, response.ChunkOrderSeedUsed);
    }

    [Fact]
    public async Task shuffle_指定種子時回報該種子()
    {
        SearchReturns(
            Hit("a", "第 1 條", "內文A", 0.9),
            Hit("b", "第 2 條", "內文B", 0.8));

        var response = await Create().GetReplyAsync(Ask(order: "shuffle", seed: 7));

        // 固定規格：shuffle(2, 7) = [1,0]
        Assert.Equal(new[] { 1, 0 }, response.ChunkPermutation);
        Assert.Equal(7, response.ChunkOrderSeedUsed);
    }

    // ════════════════════════════════════════════════════════════
    //  快取模式
    // ════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null, CacheModes.CacheBlend)]
    [InlineData("", CacheModes.CacheBlend)]
    [InlineData("blend", CacheModes.CacheBlend)]
    [InlineData("lm", CacheModes.LmCache)]
    [InlineData("LMCACHE", CacheModes.LmCache)]
    [InlineData("off", CacheModes.None)]
    public async Task 快取模式經過正規化才送往引擎_也如實回報(string? requested, string expected)
    {
        ThreeHits();

        var response = await Create().GetReplyAsync(Ask(cacheMode: requested));

        Assert.Equal(expected, response.CacheMode);
        _llm.Verify(l => l.GenerateFromTokensAsync(
            It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>(),
            It.IsAny<int?>(), expected, It.IsAny<Func<string, Task>?>()), Times.Once);
    }

    [Fact]
    public async Task 模式不可用的例外原樣往外傳()
    {
        ThreeHits();
        LlmThrows(new CacheModeUnavailableException(CacheModes.None, "none 尚未部署"));

        var ex = await Assert.ThrowsAsync<CacheModeUnavailableException>(
            () => Create().GetReplyAsync(Ask(cacheMode: "none")));

        Assert.Equal(CacheModes.None, ex.Mode);
    }

    // ════════════════════════════════════════════════════════════
    //  對話歷史與追問檢索
    // ════════════════════════════════════════════════════════════

    private void RoomHasHistory(params ConversationMessage[] messages)
        => _conversations
            .Setup(c => c.RecentMessagesAsync(RoomId, 8, It.IsAny<CancellationToken>()))
            .ReturnsAsync(messages.ToList());

    [Fact]
    public async Task 沒指定聊天室時不讀歷史_也不寫入()
    {
        ThreeHits();

        var response = await Create().GetReplyAsync(Ask());

        Assert.Null(Assert.Single(_builtHistory));
        Assert.False(response.Persisted);
        Assert.Null(response.ConversationId);
        _conversations.Verify(c => c.RecentMessagesAsync(
            It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_appended);
    }

    [Fact]
    public async Task 資料庫未設定時視同沒指定聊天室()
    {
        ThreeHits();
        _conversations.SetupGet(c => c.IsConfigured).Returns(false);

        var response = await Create().GetReplyAsync(Ask(room: RoomId));

        Assert.Null(Assert.Single(_builtHistory));
        Assert.False(response.Persisted);
        Assert.Empty(_appended);
    }

    [Fact]
    public async Task 歷史訊息帶進_prompt_並回報使用則數()
    {
        ThreeHits();
        RoomHasHistory(Stored("user", "先前問題"), Stored("assistant", "先前回答"));

        var response = await Create().GetReplyAsync(Ask("本次問題", RoomId));

        var history = Assert.Single(_builtHistory)!;
        Assert.Equal(
            new[] { new ConversationTurn("user", "先前問題"), new ConversationTurn("assistant", "先前回答") },
            history);
        Assert.Equal(2, response.HistoryMessagesUsed);
        Assert.Equal(RoomId, response.ConversationId);
    }

    [Fact]
    public async Task 歷史則數上限為零時不讀歷史()
    {
        ThreeHits();

        await Create(s => s.MaxHistoryMessages = 0).GetReplyAsync(Ask(room: RoomId));

        Assert.Null(Assert.Single(_builtHistory));
        // 寫入階段會用 limit = 1 查最後一則來去重，除此之外不該再讀
        _conversations.Verify(c => c.RecentMessagesAsync(
            It.IsAny<Guid>(), It.Is<int>(n => n > 1), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task 讀歷史失敗時退回單輪回答_問答不失敗()
    {
        ThreeHits();
        _conversations
            .Setup(c => c.RecentMessagesAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("資料庫斷線"));

        var response = await Create().GetReplyAsync(Ask(room: RoomId));

        Assert.Equal("答案", response.RawReply);
        Assert.Null(Assert.Single(_builtHistory));
        Assert.Equal(0, response.HistoryMessagesUsed);
    }

    [Fact]
    public async Task 有上一個提問時另外用合併文字做一次檢索()
    {
        ThreeHits();
        RoomHasHistory(Stored("user", "先前問題"), Stored("assistant", "先前回答"));

        await Create().GetReplyAsync(Ask("本次問題", RoomId));

        _embedding.Verify(e => e.GenerateEmbeddingAsync("本次問題", It.IsAny<CancellationToken>()), Times.Once);
        _embedding.Verify(e => e.GenerateEmbeddingAsync("先前問題\n本次問題", It.IsAny<CancellationToken>()), Times.Once);
        _qdrant.Verify(q => q.SearchSimilarAsync(
            It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>()), Times.Exactly(2));
    }

    [Fact]
    public async Task 追問靠上一提問多找到的條文會標示_且合併後依分數排序()
    {
        _embedding.Setup(e => e.GenerateEmbeddingAsync("本次問題", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { 1f });
        _embedding.Setup(e => e.GenerateEmbeddingAsync("先前問題\n本次問題", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { 2f });
        SearchReturnsFor(1f, Hit("a", "第 1 條", "內文A", 0.5));
        SearchReturnsFor(2f, Hit("a", "第 1 條", "內文A", 0.7), Hit("b", "第 2 條", "內文B", 0.6));
        RoomHasHistory(Stored("user", "先前問題"));

        var response = await Create().GetReplyAsync(Ask("本次問題", RoomId));

        Assert.True(response.RetrievalExpandedByHistory);
        // 同一片段取較高分（A 的 0.7），再依分數由高到低；B 是靠上一提問才找到的
        Assert.Equal(new[] { "內文A", "內文B" }, Contents(Assert.Single(_builtChunks)));
    }

    [Fact]
    public async Task 合併後仍受_topK_限制_被擠掉的新條文不算擴充()
    {
        _embedding.Setup(e => e.GenerateEmbeddingAsync("本次問題", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { 1f });
        _embedding.Setup(e => e.GenerateEmbeddingAsync("先前問題\n本次問題", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { 2f });
        SearchReturnsFor(1f, Hit("a", "第 1 條", "內文A", 0.9));
        SearchReturnsFor(2f, Hit("b", "第 2 條", "內文B", 0.6));
        RoomHasHistory(Stored("user", "先前問題"));

        var response = await Create(s => s.RetrievalTopK = 1).GetReplyAsync(Ask("本次問題", RoomId));

        Assert.False(response.RetrievalExpandedByHistory);
        Assert.Equal(new[] { "內文A" }, Contents(Assert.Single(_builtChunks)));
    }

    [Fact]
    public async Task 兩次檢索結果相同時不算擴充()
    {
        ThreeHits();
        RoomHasHistory(Stored("user", "先前問題"));

        var response = await Create().GetReplyAsync(Ask("本次問題", RoomId));

        Assert.False(response.RetrievalExpandedByHistory);
        Assert.Equal(3, Assert.Single(_builtChunks).Count);
    }

    [Fact]
    public async Task 追問檢索關閉時只做一次檢索()
    {
        ThreeHits();
        RoomHasHistory(Stored("user", "先前問題"));

        await Create(s => s.RetrievalHistoryUserTurns = 0).GetReplyAsync(Ask("本次問題", RoomId));

        _embedding.Verify(e => e.GenerateEmbeddingAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task 歷史裡沒有使用者提問時只做一次檢索()
    {
        ThreeHits();
        RoomHasHistory(Stored("assistant", "只有回答"), Stored("user", "   "));

        var response = await Create().GetReplyAsync(Ask("本次問題", RoomId));

        _embedding.Verify(e => e.GenerateEmbeddingAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(response.RetrievalExpandedByHistory);
    }

    // ════════════════════════════════════════════════════════════
    //  寫入聊天室
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task 成功時先存提問再存模型原始輸出_附量測欄位()
    {
        ThreeHits();

        var response = await Create().GetReplyAsync(Ask("本次問題", RoomId, cacheMode: "lmcache"));

        Assert.True(response.Persisted);
        Assert.Equal(2, _appended.Count);

        var question = _appended[0];
        Assert.Equal("user", question.Role);
        Assert.Equal("本次問題", question.Content);
        Assert.Equal(RoomId, question.ConversationId);

        var answer = _appended[1];
        Assert.Equal("assistant", answer.Role);
        Assert.Equal("答案", answer.Content);                       // 原始輸出，不含標題與免責聲明
        Assert.DoesNotContain("免責聲明", answer.Content);
        Assert.Equal(CacheModes.LmCache, answer.CacheMode);
        Assert.Equal(ChunkOrderStrategy.Default, answer.ChunkOrder);
        Assert.Equal(50, answer.TtftMs);
        Assert.Equal(1000, answer.PromptTokens);
        Assert.Equal(800, answer.CachedTokens);
        Assert.Equal(80.0, answer.CacheHitRate);
        Assert.Equal(new[] { "第 1 條", "第 2 條", "第 3 條" }, answer.RetrievedArticles);
    }

    [Fact]
    public async Task 聊天室最後一則已是同一句提問時不重複寫入()
    {
        ThreeHits();
        _conversations
            .Setup(c => c.RecentMessagesAsync(RoomId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage> { Stored("user", "本次問題") });

        var response = await Create().GetReplyAsync(Ask("本次問題", RoomId));

        Assert.True(response.Persisted);
        Assert.Equal(new[] { "assistant" }, _appended.Select(m => m.Role));
    }

    [Fact]
    public async Task 最後一則是不同的提問或回答時照常寫入提問()
    {
        ThreeHits();
        _conversations
            .Setup(c => c.RecentMessagesAsync(RoomId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage> { Stored("assistant", "本次問題") });

        await Create().GetReplyAsync(Ask("本次問題", RoomId));

        Assert.Equal(new[] { "user", "assistant" }, _appended.Select(m => m.Role));
    }

    [Fact]
    public async Task 寫入失敗不影響回答_只標示未保存()
    {
        ThreeHits();
        _conversations
            .Setup(c => c.AppendAsync(It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("寫不進去"));

        var response = await Create().GetReplyAsync(Ask(room: RoomId));

        Assert.Equal("答案", response.RawReply);
        Assert.False(response.Persisted);
    }

    // ════════════════════════════════════════════════════════════
    //  失敗時的補救：至少把提問留在聊天室
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task 回答失敗時只補存提問_不存殘缺的回答_並照常拋出原例外()
    {
        ThreeHits();
        LlmThrows(new InvalidOperationException("引擎掛了"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().GetReplyAsync(Ask("本次問題", RoomId)));

        Assert.Equal("引擎掛了", ex.Message);
        var saved = Assert.Single(_appended);
        Assert.Equal("user", saved.Role);
        Assert.Equal("本次問題", saved.Content);
    }

    [Fact]
    public async Task 失敗後重試_紀錄裡不會有兩則一模一樣的提問()
    {
        ThreeHits();
        _conversations
            .Setup(c => c.RecentMessagesAsync(RoomId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage> { Stored("user", "本次問題") });
        LlmThrows(new InvalidOperationException("引擎掛了"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().GetReplyAsync(Ask("本次問題", RoomId)));

        Assert.Empty(_appended);
    }

    [Fact]
    public async Task 補存提問本身失敗時_蓋不掉原本的錯誤()
    {
        ThreeHits();
        LlmThrows(new InvalidOperationException("引擎掛了"));
        _conversations
            .Setup(c => c.AppendAsync(It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("補存也失敗"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().GetReplyAsync(Ask(room: RoomId)));

        Assert.Equal("引擎掛了", ex.Message);
    }

    [Fact]
    public async Task 沒指定聊天室時失敗不寫任何東西()
    {
        ThreeHits();
        LlmThrows(new InvalidOperationException("引擎掛了"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().GetReplyAsync(Ask()));

        Assert.Empty(_appended);
    }

    [Fact]
    public async Task 呼叫端取消時例外往外傳_補存提問不沿用已取消的權杖()
    {
        ThreeHits();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _embedding
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create().GetReplyAsync(Ask(room: RoomId), cts.Token));

        // 提問仍被補存，且寫入用的是 CancellationToken.None
        var saved = Assert.Single(_appended);
        Assert.Equal("user", saved.Role);
        _conversations.Verify(c => c.AppendAsync(
            It.IsAny<ConversationMessage>(), It.Is<CancellationToken>(t => !t.IsCancellationRequested)),
            Times.Once);
    }

    [Fact]
    public async Task 向量資料庫初始化失敗時整個請求失敗()
    {
        _qdrant.Setup(q => q.InitializeCollectionAsync(It.IsAny<string>()))
               .ThrowsAsync(new InvalidOperationException("Qdrant 連不上"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().GetReplyAsync(Ask()));

        _llm.VerifyNoOtherCalls();
    }
}
