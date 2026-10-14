using Microsoft.AspNetCore.Hosting;

namespace ChatBot.Api.Tests.Services;

public class IngestionJobTests
{
    [Fact]
    public void 剛建立的工作處於排隊中_用掉的時間隨現在時間增加()
    {
        var job = new IngestionJob();

        Assert.Equal(IngestionStage.Queued, job.Stage);
        Assert.False(string.IsNullOrEmpty(job.Id));
        Assert.True(job.ElapsedMilliseconds >= 0);
    }

    [Fact]
    public void 已結束的工作用掉的時間固定為結束減開始()
    {
        var start = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
        var job = new IngestionJob { StartedAt = start, FinishedAt = start.AddMilliseconds(1500) };

        Assert.Equal(1500, job.ElapsedMilliseconds);
    }
}

public class IngestionJobStoreTests
{
    [Fact]
    public void 建立後可以用_id_取回_欄位照傳入值()
    {
        var store = new IngestionJobStore();

        var job = store.Create("a.pdf", "公司法", warmup: true);

        Assert.Same(job, store.Get(job.Id));
        Assert.Equal("a.pdf", job.FileName);
        Assert.Equal("公司法", job.Title);
        Assert.True(job.WarmupRequested);
    }

    [Fact]
    public void 查不到的_id_回_null()
    {
        Assert.Null(new IngestionJobStore().Get("nope"));
    }

    [Fact]
    public void Recent_受_limit_限制()
    {
        var store = new IngestionJobStore();
        for (var i = 0; i < 5; i++) store.Create($"{i}.pdf", "", warmup: false);

        Assert.Equal(3, store.Recent(3).Count);
        Assert.Equal(5, store.Recent().Count);
    }

    [Fact]
    public void 保留上限一百筆_超過時淘汰最舊的()
    {
        var store = new IngestionJobStore();

        for (var i = 0; i < 105; i++) store.Create($"{i}.pdf", "", warmup: false);

        Assert.Equal(100, store.Recent(1000).Count);
    }
}

public class IngestionServiceTests : IDisposable
{
    private const string Collection = "legal_documents";

    private readonly Mock<IObjectStorage> _storage = new();
    private readonly Mock<ILegalDocumentParser> _parser = new();
    private readonly Mock<IEmbeddingService> _embedding = new();
    private readonly Mock<IQdrantService> _qdrant = new();
    private readonly Mock<IKvWarmupService> _warmup = new();
    private readonly List<(string Id, KeyValuePair<string, string>[] Payload)> _upserts = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ingest-test-" + Guid.NewGuid().ToString("N"));

    public IngestionServiceTests()
    {
        Directory.CreateDirectory(_tempDir);

        _storage.SetupGet(s => s.BackendName).Returns("test-backend");
        _storage
            .Setup(s => s.SaveAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObject { Key = "202610/abc_a.pdf" });
        _storage
            .Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _parser
            .Setup(p => p.ParsePdfAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Parsed());

        _embedding
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { 0.5f });

        _qdrant
            .Setup(q => q.UpsertDocumentAsync(
                Collection, It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<KeyValuePair<string, string>[]>()))
            .Callback<string, string, float[], KeyValuePair<string, string>[]>((_, id, _, payload) => _upserts.Add((id, payload)))
            .Returns(Task.CompletedTask);

        _warmup
            .Setup(w => w.WarmupAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<WarmupProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WarmupProgress { TotalChunks = 2, ProcessedChunks = 2 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 暫存目錄清不掉不影響測試結果 */ }
    }

    private static ParseResult Parsed(params string[] articleLabels)
    {
        var labels = articleLabels.Length > 0 ? articleLabels : new[] { "第 1 條", "第 2 條" };
        return new ParseResult
        {
            Title = "公司法",
            ArticleCount = labels.Length,
            Chunks = labels
                .Select((label, i) => new ParsedChunk
                {
                    Id = $"id-{i}",
                    Title = "公司法",
                    Content = $"{label}\n內文{i}",
                    Chapter = "總則",
                    Article = label,
                    NChars = 6,
                    SourceFile = "a.pdf",
                })
                .ToList(),
        };
    }

    private IngestionService Create()
        => new(_storage.Object, _parser.Object, _embedding.Object, _qdrant.Object, _warmup.Object,
            TestFactory.Options(), NullLogger<IngestionService>.Instance);

    private string TempPdf()
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(path, "%PDF-fake");
        return path;
    }

    private static IngestionJob Job(string title = "", bool warmup = true)
        => new() { FileName = "a.pdf", Title = title, WarmupRequested = warmup };

    // ── 成功路徑 ────────────────────────────────────────────────

    [Fact]
    public async Task 完整流程跑完_階段為_Done_計數正確()
    {
        _qdrant.Setup(q => q.DeleteByTitleAsync(Collection, "公司法", It.IsAny<CancellationToken>()))
               .ReturnsAsync(4);
        var job = Job();
        var path = TempPdf();

        await Create().RunAsync(job, path);

        Assert.Equal(IngestionStage.Done, job.Stage);
        Assert.Null(job.Error);
        Assert.NotNull(job.FinishedAt);
        Assert.Equal("test-backend", job.StorageBackend);
        Assert.Equal("202610/abc_a.pdf", job.StorageKey);
        Assert.Equal("公司法", job.Title);                 // 標題以解析結果為準
        Assert.Equal(2, job.ArticleCount);
        Assert.Equal(2, job.ChunkCount);
        Assert.Equal(2, job.EmbeddedCount);
        Assert.Equal(4, job.ReplacedCount);
        Assert.NotNull(job.Warmup);
        _qdrant.Verify(q => q.InitializeCollectionAsync(Collection), Times.Once);
    }

    [Fact]
    public async Task 逐片段寫入向量庫_payload_帶來源檔案與儲存位置()
    {
        await Create().RunAsync(Job(), TempPdf());

        Assert.Equal(new[] { "id-0", "id-1" }, _upserts.Select(u => u.Id));
        var payload = _upserts[0].Payload.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("公司法", payload["title"]);
        Assert.Equal("第 1 條\n內文0", payload["content"]);   // 內文已含條號行，不得再加標記
        Assert.Equal("總則", payload["chapter"]);
        Assert.Equal("第 1 條", payload["articleNumber"]);
        Assert.Equal("6", payload["nChars"]);
        Assert.Equal("a.pdf", payload["sourceFile"]);
        Assert.Equal("202610/abc_a.pdf", payload["storageKey"]);
    }

    [Fact]
    public async Task 工作沒有標題時解析器收到_null_有標題時照傳()
    {
        var service = Create();

        await service.RunAsync(Job(title: "  "), TempPdf());
        await service.RunAsync(Job(title: "民法"), TempPdf());

        _parser.Verify(p => p.ParsePdfAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()), Times.Once);
        _parser.Verify(p => p.ParsePdfAsync(It.IsAny<string>(), "民法", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task 沒要求預熱就不預熱()
    {
        var job = Job(warmup: false);

        await Create().RunAsync(job, TempPdf());

        Assert.Equal(IngestionStage.Done, job.Stage);
        Assert.Null(job.Warmup);
        _warmup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 要求預熱時用全部片段的全文預熱()
    {
        await Create().RunAsync(Job(warmup: true), TempPdf());

        _warmup.Verify(w => w.WarmupAsync(
            It.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "第 1 條\n內文0", "第 2 條\n內文1" })),
            null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task 結束後一律刪掉暫存檔()
    {
        var path = TempPdf();

        await Create().RunAsync(Job(), path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task 原始檔以_pdf_內容型別存入物件儲存()
    {
        await Create().RunAsync(Job(), TempPdf());

        _storage.Verify(s => s.SaveAsync(
            It.IsAny<Stream>(), "a.pdf", "application/pdf", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 警告 ───────────────────────────────────────────────────

    [Fact]
    public async Task 解析器的警告會留在工作上_但有片段時仍算成功()
    {
        var parsed = Parsed();
        parsed.Warning = "有一頁掃描品質不佳";
        _parser.Setup(p => p.ParsePdfAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(parsed);
        var job = Job();

        await Create().RunAsync(job, TempPdf());

        Assert.Equal(IngestionStage.Done, job.Stage);
        Assert.Contains("有一頁掃描品質不佳", job.Warnings);
    }

    [Fact]
    public async Task 偵測到重複的條號標籤時加警告_這是切分失效最靈敏的指標()
    {
        _parser.Setup(p => p.ParsePdfAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(Parsed("第 1 條", "第 1 條", "第 2 條"));
        var job = Job();

        await Create().RunAsync(job, TempPdf());

        var warning = Assert.Single(job.Warnings);
        Assert.Contains("1 組重複的條號標籤", warning);
        Assert.Contains("涉及 2 個片段", warning);
        Assert.Contains("第 1 條", warning);
    }

    [Fact]
    public async Task 條號標籤為空的片段不算重複()
    {
        _parser.Setup(p => p.ParsePdfAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(Parsed("", "", "第 2 條"));
        var job = Job();

        await Create().RunAsync(job, TempPdf());

        Assert.Empty(job.Warnings);
    }

    // ── 失敗與善後 ─────────────────────────────────────────────

    [Fact]
    public async Task 解析不出片段時失敗_清掉沒人引用的原始檔()
    {
        _parser.Setup(p => p.ParsePdfAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new ParseResult { Title = "公司法", Warning = "找不到條號" });
        var job = Job();
        var path = TempPdf();

        await Create().RunAsync(job, path);

        Assert.Equal(IngestionStage.Failed, job.Stage);
        Assert.Equal("找不到條號", job.Error);
        Assert.NotNull(job.FinishedAt);
        _storage.Verify(s => s.DeleteAsync("202610/abc_a.pdf", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(job.StorageKey);
        Assert.Contains(job.Warnings, w => w.Contains("已自動清除"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task 解析不出片段也沒有警告時用預設的錯誤說明()
    {
        _parser.Setup(p => p.ParsePdfAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new ParseResult { Title = "公司法" });
        var job = Job();

        await Create().RunAsync(job, TempPdf());

        Assert.Contains("條列式法規", job.Error);
    }

    [Fact]
    public async Task 已有片段寫入後才失敗_原始檔保留_因為片段的_payload_指向它()
    {
        _embedding.SetupSequence(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { 0.5f })
                  .ThrowsAsync(new HttpRequestException("embedding 掛了"));
        var job = Job();

        await Create().RunAsync(job, TempPdf());

        Assert.Equal(IngestionStage.Failed, job.Stage);
        Assert.Equal(1, job.EmbeddedCount);
        Assert.Contains("embedding 掛了", job.Error);
        _storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("202610/abc_a.pdf", job.StorageKey);
        Assert.Contains(job.Warnings, w => w.Contains("已有 1 個片段寫入知識庫"));
    }

    [Fact]
    public async Task 預熱階段失敗_片段早已寫入_不清原始檔()
    {
        _warmup.Setup(w => w.WarmupAsync(
                    It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<WarmupProgress>?>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("引擎掛了"));
        var job = Job(warmup: true);

        await Create().RunAsync(job, TempPdf());

        Assert.Equal(IngestionStage.Failed, job.Stage);
        Assert.Equal(2, job.EmbeddedCount);
        _storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task 原始檔還沒存成功就失敗_沒有東西可清()
    {
        _storage
            .Setup(s => s.SaveAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("磁碟滿了"));
        var job = Job();

        await Create().RunAsync(job, TempPdf());

        Assert.Equal(IngestionStage.Failed, job.Stage);
        Assert.Contains("磁碟滿了", job.Error);
        _storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task 暫存檔不存在時整個工作失敗_但不丟例外給呼叫端()
    {
        var job = Job();

        var exception = await Record.ExceptionAsync(
            () => Create().RunAsync(job, Path.Combine(_tempDir, "missing.pdf")));

        Assert.Null(exception);
        Assert.Equal(IngestionStage.Failed, job.Stage);
    }

    [Fact]
    public async Task 清除孤兒檔案本身失敗_不蓋掉原本的錯誤_並提醒手動處理()
    {
        _parser.Setup(p => p.ParsePdfAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new ParseResult { Title = "公司法", Warning = "解析失敗" });
        _storage.Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("刪不掉"));
        var job = Job();

        await Create().RunAsync(job, TempPdf());

        Assert.Equal("解析失敗", job.Error);
        Assert.Contains(job.Warnings, w => w.Contains("需手動處理") && w.Contains("202610/abc_a.pdf"));
        Assert.Equal("202610/abc_a.pdf", job.StorageKey);
    }

    [Fact]
    public async Task 儲存層回報沒刪到東西時不謊稱已清除()
    {
        _parser.Setup(p => p.ParsePdfAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new ParseResult { Title = "公司法" });
        _storage.Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        var job = Job();

        await Create().RunAsync(job, TempPdf());

        Assert.DoesNotContain(job.Warnings, w => w.Contains("已自動清除"));
        Assert.Equal("202610/abc_a.pdf", job.StorageKey);
    }

    [Fact]
    public async Task 取消時工作標示失敗_已寫入的數量照實記錄()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var job = Job();

        await Create().RunAsync(job, TempPdf(), cts.Token);

        Assert.Equal(IngestionStage.Failed, job.Stage);
        Assert.Equal(0, job.EmbeddedCount);
    }
}

public class LocalObjectStorageTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "storage-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public LocalObjectStorageTests()
    {
        _root = Path.Combine(_tempDir, "root");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 暫存目錄清不掉不影響測試結果 */ }
    }

    private LocalObjectStorage Create(string? configuredRoot = null, string contentRoot = "/unused")
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(contentRoot);

        return new LocalObjectStorage(
            TestFactory.Options(s => s.ObjectStorageRoot = configuredRoot ?? _root),
            NullLogger<LocalObjectStorage>.Instance,
            env.Object);
    }

    private static MemoryStream Bytes(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void 後端名稱是_local_disk()
    {
        Assert.Equal("local-disk", Create().BackendName);
    }

    [Fact]
    public void 建構時建立根目錄_相對路徑接在內容根目錄之下()
    {
        _ = Create(configuredRoot: "uploads", contentRoot: _tempDir);

        Assert.True(Directory.Exists(Path.Combine(_tempDir, "uploads")));
    }

    [Fact]
    public async Task 存檔後回傳相對_key_與大小_內容完整()
    {
        var storage = Create();

        var stored = await storage.SaveAsync(Bytes("%PDF-內容"), "公司法.pdf", "application/pdf");

        Assert.EndsWith("_公司法.pdf", stored.Key);
        Assert.StartsWith(DateTime.UtcNow.ToString("yyyyMM") + "/", stored.Key);
        Assert.Equal("公司法.pdf", stored.FileName);
        Assert.Equal("application/pdf", stored.ContentType);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("%PDF-內容"), stored.SizeBytes);
        Assert.Equal("%PDF-內容", File.ReadAllText(stored.Location));
    }

    [Fact]
    public async Task 同名檔案不會互相覆蓋()
    {
        var storage = Create();

        var first = await storage.SaveAsync(Bytes("一"), "a.pdf", "application/pdf");
        var second = await storage.SaveAsync(Bytes("二"), "a.pdf", "application/pdf");

        Assert.NotEqual(first.Key, second.Key);
        Assert.Equal("一", File.ReadAllText(first.Location));
    }

    [Fact]
    public async Task 檔名裡的路徑片段被拿掉_檔案不會寫到根目錄之外()
    {
        var storage = Create();

        var stored = await storage.SaveAsync(Bytes("x"), "../../evil.pdf", "application/pdf");

        Assert.Equal("evil.pdf", stored.FileName);
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(stored.Location));
        Assert.False(File.Exists(Path.Combine(_tempDir, "evil.pdf")));
    }

    [Fact]
    public async Task 可以用_key_取回內容_不存在則回_null()
    {
        var storage = Create();
        var stored = await storage.SaveAsync(Bytes("內容"), "a.pdf", "application/pdf");

        await using (var stream = await storage.OpenAsync(stored.Key))
        {
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            Assert.Equal("內容", await reader.ReadToEndAsync());
        }

        Assert.Null(await storage.OpenAsync("202610/不存在.pdf"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 空白_key_取不到也刪不掉(string key)
    {
        var storage = Create();

        Assert.Null(await storage.OpenAsync(key));
        Assert.False(await storage.DeleteAsync(key));
    }

    [Fact]
    public async Task key_試圖跳出根目錄時一律拒絕_不會讀到也不會刪到外面的檔案()
    {
        var storage = Create();
        var outside = Path.Combine(_tempDir, "secret.txt");
        File.WriteAllText(outside, "機密");

        Assert.Null(await storage.OpenAsync("../secret.txt"));
        Assert.False(await storage.DeleteAsync("../secret.txt"));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task 刪除成功回_true_檔案消失_再刪一次回_false()
    {
        var storage = Create();
        var stored = await storage.SaveAsync(Bytes("x"), "a.pdf", "application/pdf");

        Assert.True(await storage.DeleteAsync(stored.Key));
        Assert.False(File.Exists(stored.Location));
        Assert.False(await storage.DeleteAsync(stored.Key));
    }
}
