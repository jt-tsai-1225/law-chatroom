using ChatBot.Api.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Api.Tests.Controllers;

public class CacheControllerTests
{
    private readonly Mock<ILMCacheService> _cache = new();
    private readonly Mock<IChunkCacheTracker> _tracker = new();

    private CacheController Create()
        => new(_cache.Object, _tracker.Object, NullLogger<CacheController>.Instance);

    private void CacheHas(bool enabled, long hits, long misses, long entries, long bytes)
    {
        _cache.SetupGet(c => c.IsEnabled).Returns(enabled);
        _cache.Setup(c => c.GetStats()).Returns(new CacheStats
        {
            HitCount = hits,
            MissCount = misses,
            TotalEntries = entries,
            TotalSizeBytes = bytes,
        });
    }

    [Fact]
    public void GetLMCacheStats_回報命中率與以_KB_計的大小()
    {
        CacheHas(enabled: true, hits: 3, misses: 1, entries: 2, bytes: 2048);

        var result = Create().GetLMCacheStats();

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(json.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(3, json.GetProperty("hitCount").GetInt64());
        Assert.Equal(1, json.GetProperty("missCount").GetInt64());
        Assert.Equal(75, json.GetProperty("hitRate").GetDouble());
        Assert.Equal(2, json.GetProperty("totalEntries").GetInt64());
        Assert.Equal(2048, json.GetProperty("totalSizeBytes").GetInt64());
        Assert.Equal(2, json.GetProperty("totalSizeKB").GetDouble());
    }

    [Fact]
    public void ClearLMCache_清空快取並回報成功()
    {
        var result = Create().ClearLMCache();

        Assert.IsType<OkObjectResult>(result);
        _cache.Verify(c => c.Clear(), Times.Once);
    }

    [Fact]
    public void ClearChunkTracker_回報清掉幾筆()
    {
        _tracker.SetupGet(t => t.Count).Returns(7);

        var result = Create().ClearChunkTracker();

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(7, json.GetProperty("clearedEntries").GetInt32());
        _tracker.Verify(t => t.Clear(), Times.Once);
    }

    [Fact]
    public void GetAllStats_包含回答快取與片段追蹤器兩部分()
    {
        CacheHas(enabled: false, hits: 0, misses: 0, entries: 0, bytes: 0);
        _tracker.SetupGet(t => t.Count).Returns(12);

        var result = Create().GetAllStats();

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(json.GetProperty("lmcache").GetProperty("isEnabled").GetBoolean());
        Assert.Equal(0, json.GetProperty("lmcache").GetProperty("hitRate").GetDouble());
        Assert.Equal(12, json.GetProperty("chunkTracker").GetProperty("trackedChunks").GetInt32());
    }
}

public class ConversationsControllerTests
{
    private readonly Mock<IConversationStore> _store = new();

    private ConversationsController Create(bool configured = true)
    {
        _store.SetupGet(s => s.IsConfigured).Returns(configured);
        return new ConversationsController(_store.Object, NullLogger<ConversationsController>.Instance);
    }

    private static int StatusOf(IActionResult? result)
        => Assert.IsType<ObjectResult>(result).StatusCode!.Value;

    private static Conversation Sample(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Title = "公司重整",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ── 未設定資料庫：整組端點回 503，而不是假裝成功 ────────────

    [Fact]
    public async Task 未設定資料庫時每個端點都回_503()
    {
        var controller = Create(configured: false);
        var id = Guid.NewGuid();

        Assert.Equal(503, StatusOf((await controller.List(default)).Result));
        Assert.Equal(503, StatusOf((await controller.Get(id, default)).Result));
        Assert.Equal(503, StatusOf((await controller.Create(null, default)).Result));
        Assert.Equal(503, StatusOf(await controller.Rename(id, new RenameConversationRequest { Title = "x" }, default)));
        Assert.Equal(503, StatusOf(await controller.Delete(id, default)));
    }

    [Fact]
    public async Task 未設定資料庫時的訊息說明原因()
    {
        var controller = Create(configured: false);

        var result = await controller.List(default);

        var json = TestFactory.ToJson(Assert.IsType<ObjectResult>(result.Result).Value);
        Assert.Equal("聊天室功能未啟用", json.GetProperty("error").GetString());
        Assert.Contains("ConnectionStrings:Postgres", json.GetProperty("detail").GetString());
    }

    // ── List ────────────────────────────────────────────────────

    [Fact]
    public async Task List_回傳聊天室清單()
    {
        var list = new List<Conversation> { Sample(), Sample() };
        _store.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(list);

        var result = await Create().List(default);

        Assert.Same(list, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task List_資料庫失敗時回_503_並帶錯誤訊息()
    {
        _store.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("連不上"));

        var result = await Create().List(default);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, obj.StatusCode);
        Assert.Equal("連不上", TestFactory.ToJson(obj.Value).GetProperty("detail").GetString());
    }

    // ── Get ─────────────────────────────────────────────────────

    [Fact]
    public async Task Get_找到時回傳完整聊天室()
    {
        var id = Guid.NewGuid();
        var detail = new ConversationDetail { Conversation = Sample(id) };
        _store.Setup(s => s.GetAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(detail);

        var result = await Create().Get(id, default);

        Assert.Same(detail, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Get_找不到時回_404()
    {
        _store.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((ConversationDetail?)null);

        var result = await Create().Get(Guid.NewGuid(), default);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Get_資料庫失敗時回_503()
    {
        _store.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new Exception("boom"));

        var result = await Create().Get(Guid.NewGuid(), default);

        Assert.Equal(503, StatusOf(result.Result));
    }

    // ── Create ──────────────────────────────────────────────────

    [Fact]
    public async Task Create_成功時回_201_並指向_Get()
    {
        var created = Sample();
        _store.Setup(s => s.CreateAsync("新聊天室", It.IsAny<CancellationToken>())).ReturnsAsync(created);

        var result = await Create().Create(new CreateConversationRequest { Title = "新聊天室" }, default);

        var createdAt = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(ConversationsController.Get), createdAt.ActionName);
        Assert.Equal(created.Id, createdAt.RouteValues!["id"]);
        Assert.Same(created, createdAt.Value);
    }

    [Fact]
    public async Task Create_沒帶請求本文時標題傳_null_由後端取預設名()
    {
        _store.Setup(s => s.CreateAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Sample());

        var result = await Create().Create(null, default);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        _store.Verify(s => s.CreateAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_資料庫失敗時回_503()
    {
        _store.Setup(s => s.CreateAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new Exception("boom"));

        var result = await Create().Create(new CreateConversationRequest(), default);

        Assert.Equal(503, StatusOf(result.Result));
    }

    // ── Rename ──────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rename_標題為空回_400_且不碰資料庫(string title)
    {
        var controller = Create();

        var result = await controller.Rename(
            Guid.NewGuid(), new RenameConversationRequest { Title = title }, default);

        Assert.IsType<BadRequestObjectResult>(result);
        _store.Verify(s => s.RenameAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rename_成功回_204()
    {
        var id = Guid.NewGuid();
        _store.Setup(s => s.RenameAsync(id, "新標題", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Create().Rename(id, new RenameConversationRequest { Title = "新標題" }, default);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Rename_找不到回_404()
    {
        _store.Setup(s => s.RenameAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);

        var result = await Create().Rename(
            Guid.NewGuid(), new RenameConversationRequest { Title = "x" }, default);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Rename_資料庫失敗回_503()
    {
        _store.Setup(s => s.RenameAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new Exception("boom"));

        var result = await Create().Rename(
            Guid.NewGuid(), new RenameConversationRequest { Title = "x" }, default);

        Assert.Equal(503, StatusOf(result));
    }

    // ── Delete ──────────────────────────────────────────────────

    [Fact]
    public async Task Delete_成功回_204_找不到回_404_失敗回_503()
    {
        var id = Guid.NewGuid();
        var controller = Create();

        _store.Setup(s => s.DeleteAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        Assert.IsType<NoContentResult>(await controller.Delete(id, default));

        _store.Setup(s => s.DeleteAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        Assert.IsType<NotFoundObjectResult>(await controller.Delete(id, default));

        _store.Setup(s => s.DeleteAsync(id, It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("boom"));
        Assert.Equal(503, StatusOf(await controller.Delete(id, default)));
    }
}

public class CacheModesControllerTests
{
    [Fact]
    public async Task Get_原樣回傳三種模式的狀態()
    {
        var statuses = new List<CacheModeStatus>
        {
            new() { Mode = CacheModes.CacheBlend, DisplayName = "CacheBlend", Configured = true, Reachable = true },
            new() { Mode = CacheModes.None, DisplayName = "無快取", Configured = false },
        };
        var registry = new Mock<ILlmEndpointRegistry>();
        registry.Setup(r => r.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(statuses);

        var result = await new CacheModesController(registry.Object).Get(default);

        Assert.Same(statuses, Assert.IsType<OkObjectResult>(result.Result).Value);
    }
}
