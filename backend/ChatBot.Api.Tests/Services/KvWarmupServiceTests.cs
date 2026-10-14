namespace ChatBot.Api.Tests.Services;

public class KvWarmupServiceTests
{
    private readonly Mock<IPromptBuilder> _promptBuilder = new();
    private readonly Mock<ILLMService> _llm = new();
    private readonly Mock<IChunkCacheTracker> _tracker = new();

    public KvWarmupServiceTests()
    {
        _promptBuilder
            .Setup(p => p.BuildAsync(
                It.IsAny<IReadOnlyList<RetrievedChunk>>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ConversationTurn>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BuiltPrompt
            {
                Tokens = new[] { 1, 2, 3 },
                ChunkCount = 1,
                ChunkOffsets = new[] { 0 },
                ChunkTokenCounts = new[] { 1 },
                PrefixTokens = 0,
            });
    }

    private void LlmReturns(int promptTokens = 100, int cachedTokens = 40)
        => _llm
            .Setup(l => l.GenerateFromTokensAsync(
                It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<Func<string, Task>?>()))
            .ReturnsAsync(new LLMCompletionResult
            {
                Content = "x",
                PromptTokens = promptTokens,
                CachedTokens = cachedTokens,
            });

    private KvWarmupService Create(int batchSize = 5)
        => new(
            _promptBuilder.Object,
            _llm.Object,
            _tracker.Object,
            TestFactory.Options(s => s.WarmupBatchSize = batchSize),
            NullLogger<KvWarmupService>.Instance);

    private static List<string> Chunks(int n)
        => Enumerable.Range(1, n).Select(i => $"條文-{i}").ToList();

    [Fact]
    public async Task 依批次大小切批並累計_token_統計()
    {
        LlmReturns(promptTokens: 100, cachedTokens: 40);
        var service = Create(batchSize: 5);

        var result = await service.WarmupAsync(Chunks(12));

        Assert.Equal(12, result.TotalChunks);
        Assert.Equal(12, result.ProcessedChunks);
        Assert.Equal(3, result.Batches);            // 5 + 5 + 2
        Assert.Equal(0, result.FailedBatches);
        Assert.Equal(300, result.TotalPromptTokens);
        Assert.Equal(120, result.TotalCachedTokens);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task 每批只要求產生一個_token_且不帶對話歷史()
    {
        LlmReturns();
        var service = Create(batchSize: 5);

        await service.WarmupAsync(Chunks(5));

        _llm.Verify(l => l.GenerateFromTokensAsync(
            It.IsAny<IReadOnlyList<int>>(),
            It.IsAny<CancellationToken>(),
            It.Is<int?>(m => m == 1),
            It.IsAny<string?>(),
            It.IsAny<Func<string, Task>?>()), Times.Once);

        _promptBuilder.Verify(p => p.BuildAsync(
            It.Is<IReadOnlyList<RetrievedChunk>>(c => c.Count == 5),
            "請摘要以上條文。",
            It.Is<IReadOnlyList<ConversationTurn>?>(h => h == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task 成功的批次才登記為已送出()
    {
        LlmReturns();
        var service = Create(batchSize: 2);

        await service.WarmupAsync(Chunks(4));

        _tracker.Verify(t => t.MarkSent(It.IsAny<IEnumerable<string>>()), Times.Exactly(2));
        _tracker.Verify(t => t.MarkSent(
            It.Is<IEnumerable<string>>(s => s.SequenceEqual(new[] { "條文-1", "條文-2" }))), Times.Once);
    }

    [Fact]
    public async Task 單批失敗不中斷整體_且失敗的批次不登記()
    {
        _llm
            .SetupSequence(l => l.GenerateFromTokensAsync(
                It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<Func<string, Task>?>()))
            .ReturnsAsync(new LLMCompletionResult { PromptTokens = 10, CachedTokens = 0 })
            .ThrowsAsync(new InvalidOperationException("引擎暫時不可用"))
            .ReturnsAsync(new LLMCompletionResult { PromptTokens = 10, CachedTokens = 0 });
        var service = Create(batchSize: 5);

        var result = await service.WarmupAsync(Chunks(15));

        Assert.Equal(3, result.Batches);
        Assert.Equal(1, result.FailedBatches);
        Assert.Equal(10, result.ProcessedChunks);   // 失敗那批的 5 段不算
        var error = Assert.Single(result.Errors);
        Assert.Contains("第 2 批", error);
        Assert.Contains("引擎暫時不可用", error);
        _tracker.Verify(t => t.MarkSent(It.IsAny<IEnumerable<string>>()), Times.Exactly(2));
    }

    [Fact]
    public async Task 取消時例外往外傳_不被當成單批失敗吞掉()
    {
        _llm
            .Setup(l => l.GenerateFromTokensAsync(
                It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<Func<string, Task>?>()))
            .ThrowsAsync(new OperationCanceledException());
        var service = Create();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.WarmupAsync(Chunks(3)));
    }

    [Fact]
    public async Task 開始前就已取消則不送任何請求()
    {
        LlmReturns();
        var service = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.WarmupAsync(Chunks(3), ct: cts.Token));

        _llm.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 批次大小為零時以一為準()
    {
        LlmReturns();
        var service = Create(batchSize: 0);

        var result = await service.WarmupAsync(Chunks(3));

        Assert.Equal(3, result.Batches);
    }

    [Fact]
    public async Task 空清單不送任何請求()
    {
        var service = Create();

        var result = await service.WarmupAsync(new List<string>());

        Assert.Equal(0, result.TotalChunks);
        Assert.Equal(0, result.Batches);
        _llm.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 每批結束都回報一次進度()
    {
        LlmReturns();
        var progress = new RecordingProgress();
        var service = Create(batchSize: 5);

        await service.WarmupAsync(Chunks(12), progress);

        Assert.Equal(
            new[] { (1, 5, 0), (2, 10, 0), (3, 12, 0) },
            progress.Reports);
    }
}
