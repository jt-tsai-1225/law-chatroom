using System.Text;
using ChatBot.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBot.Api.Tests.Controllers;

public class KnowledgeBaseControllerTests
{
    private const string Collection = "legal_documents";

    private readonly Mock<IQdrantService> _qdrant = new();
    private readonly Mock<IEmbeddingService> _embedding = new();
    private readonly Mock<ILegalDocumentParser> _parser = new();
    private readonly Mock<IKvWarmupService> _warmup = new();
    private readonly Mock<IChunkCacheTracker> _tracker = new();
    private readonly Mock<IIngestionService> _ingestion = new();
    private readonly IngestionJobStore _jobs = new();

    private static KnowledgeSearchResult Chunk(string id, string title, string chapter, string content)
        => new() { Id = id, Title = title, Chapter = chapter, Content = content, ArticleNumber = "第 " + id + " 條" };

    public KnowledgeBaseControllerTests()
    {
        _embedding
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { 0.5f });

        _warmup
            .Setup(w => w.WarmupAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IProgress<WarmupProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WarmupProgress
            {
                TotalChunks = 2,
                ProcessedChunks = 2,
                Batches = 1,
                TotalPromptTokens = 100,
                TotalCachedTokens = 10,
            });
    }

    private KnowledgeBaseController Create()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ingestion.Object);

        return new KnowledgeBaseController(
            _qdrant.Object,
            _embedding.Object,
            _parser.Object,
            _warmup.Object,
            _jobs,
            _tracker.Object,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<KnowledgeBaseController>.Instance);
    }

    private void CorpusIs(params KnowledgeSearchResult[] chunks)
        => _qdrant
            .Setup(q => q.ScrollAllAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(chunks.ToList());

    private static FormFile PdfFile(string name = "公司法.pdf", string content = "%PDF-fake")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);
    }

    // ════════════════════════════════════════════════════════════
    //  上傳
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Upload_沒有檔案或檔案是空的回_400()
    {
        var controller = Create();

        Assert.IsType<BadRequestObjectResult>(await controller.Upload(null!));
        Assert.IsType<BadRequestObjectResult>(await controller.Upload(PdfFile(content: "")));
        _ingestion.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Upload_不是_PDF_回_400_並提示改用純文字端點()
    {
        var result = await Create().Upload(PdfFile("notes.txt"));

        var json = TestFactory.ToJson(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Contains("PDF", json.GetProperty("error").GetString());
        Assert.Contains("POST /api/knowledgebase", json.GetProperty("hint").GetString());
        _ingestion.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Upload_副檔名大小寫不拘()
    {
        var started = new TaskCompletionSource<string>();
        _ingestion
            .Setup(i => i.RunAsync(It.IsAny<IngestionJob>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IngestionJob, string, CancellationToken>((_, path, _) =>
            {
                started.TrySetResult(path);
                return Task.CompletedTask;
            });

        var result = await Create().Upload(PdfFile("民法.PDF"));
        var path = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        File.Delete(path);

        Assert.IsType<AcceptedResult>(result);
    }

    [Fact]
    public async Task Upload_回_202_並在背景以暫存檔啟動匯入()
    {
        var started = new TaskCompletionSource<(IngestionJob Job, string Path, string Content, CancellationToken Ct)>();
        _ingestion
            .Setup(i => i.RunAsync(It.IsAny<IngestionJob>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IngestionJob, string, CancellationToken>((job, path, ct) =>
            {
                started.TrySetResult((job, path, File.ReadAllText(path), ct));
                return Task.CompletedTask;
            });

        var result = await Create().Upload(PdfFile("公司法.pdf", "%PDF-內容"), title: "公司法", warmup: false);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var json = TestFactory.ToJson(accepted.Value);
        var jobId = json.GetProperty("jobId").GetString()!;
        Assert.Equal("公司法.pdf", json.GetProperty("fileName").GetString());
        Assert.False(json.GetProperty("warmup").GetBoolean());
        Assert.Equal($"/api/knowledgebase/jobs/{jobId}", json.GetProperty("statusUrl").GetString());

        var run = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(jobId, run.Job.Id);
            Assert.Equal("公司法", run.Job.Title);
            Assert.False(run.Job.WarmupRequested);
            Assert.Equal("%PDF-內容", run.Content);        // 暫存檔裡是上傳的內容
            Assert.False(run.Ct.CanBeCanceled);            // 背景工作不沿用請求的取消權杖
            Assert.NotNull(_jobs.Get(jobId));
        }
        finally
        {
            File.Delete(run.Path);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  工作進度
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void GetJob_找不到回_404()
    {
        Assert.IsType<NotFoundObjectResult>(Create().GetJob("nope"));
    }

    [Fact]
    public void GetJob_回報階段_計數與預熱進度()
    {
        var job = _jobs.Create("a.pdf", "公司法", warmup: true);
        job.Stage = IngestionStage.Warmup;
        job.ArticleCount = 5;
        job.ChunkCount = 3;
        job.EmbeddedCount = 3;
        job.Warmup = new WarmupProgress { TotalChunks = 3, ProcessedChunks = 2, FailedBatches = 1 };
        job.Warnings.Add("警告一");

        var result = Create().GetJob(job.Id);

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(job.Id, json.GetProperty("jobId").GetString());
        Assert.Equal("warmup", json.GetProperty("stage").GetString());       // 階段名稱轉小寫
        Assert.Equal(5, json.GetProperty("articleCount").GetInt32());
        Assert.Equal(3, json.GetProperty("embeddedCount").GetInt32());
        Assert.True(json.GetProperty("warmupRequested").GetBoolean());
        Assert.Equal(2, json.GetProperty("warmup").GetProperty("processedChunks").GetInt32());
        Assert.Equal(1, json.GetProperty("warmup").GetProperty("failedBatches").GetInt32());
        Assert.Equal("警告一", json.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public void GetJob_尚未預熱時_warmup_欄位為_null()
    {
        var job = _jobs.Create("a.pdf", "", warmup: false);

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(Create().GetJob(job.Id)).Value);

        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("warmup").ValueKind);
    }

    [Fact]
    public void ListJobs_受_limit_限制()
    {
        for (var i = 0; i < 5; i++) _jobs.Create($"{i}.pdf", "", warmup: false);

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(Create().ListJobs(limit: 3)).Value);

        Assert.Equal(3, json.GetProperty("jobs").GetArrayLength());
    }

    // ════════════════════════════════════════════════════════════
    //  純文字新增
    // ════════════════════════════════════════════════════════════

    private static ParseResult TwoChunks() => new()
    {
        Title = "公司法",
        ArticleCount = 2,
        Chunks = new List<ParsedChunk>
        {
            new() { Id = "id-1", Title = "公司法", Content = "第 1 條\n內文一", Chapter = "總則", Article = "第 1 條", NChars = 8 },
            new() { Id = "id-2", Title = "公司法", Content = "第 2 條\n內文二", Chapter = "總則", Article = "第 2 條", NChars = 8 },
        },
    };

    private void ParserReturns(ParseResult result)
        => _parser
            .Setup(p => p.ParseTextAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Theory]
    [InlineData("", "內文")]
    [InlineData("  ", "內文")]
    [InlineData("公司法", "")]
    [InlineData("公司法", "   ")]
    public async Task AddArticle_標題或內容為空回_400(string title, string content)
    {
        var result = await Create().AddArticle(new AddArticleTextRequest { Title = title, Content = content });

        Assert.IsType<BadRequestObjectResult>(result);
        _parser.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AddArticle_解析不出片段時回_400_並帶解析器的說明()
    {
        ParserReturns(new ParseResult { Title = "公司法", Warning = "找不到條號" });

        var result = await Create().AddArticle(
            new AddArticleTextRequest { Title = "公司法", Content = "亂寫的文字" });

        var json = TestFactory.ToJson(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Equal("找不到條號", json.GetProperty("error").GetString());
        Assert.Contains("獨立成行", json.GetProperty("hint").GetString());
        _qdrant.Verify(q => q.UpsertDocumentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]>(),
            It.IsAny<KeyValuePair<string, string>[]>()), Times.Never);
    }

    [Fact]
    public async Task AddArticle_沒有警告也解析不出片段時用預設說明()
    {
        ParserReturns(new ParseResult { Title = "公司法" });

        var result = await Create().AddArticle(
            new AddArticleTextRequest { Title = "公司法", Content = "x" });

        var json = TestFactory.ToJson(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Equal("解析後沒有任何片段", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AddArticle_先清掉同名舊片段再逐片段寫入_且與_PDF_路徑用同一個解析器()
    {
        ParserReturns(TwoChunks());
        _qdrant.Setup(q => q.DeleteByTitleAsync(Collection, "公司法", It.IsAny<CancellationToken>()))
               .ReturnsAsync(3);
        var written = new List<(string Id, KeyValuePair<string, string>[] Payload)>();
        _qdrant
            .Setup(q => q.UpsertDocumentAsync(
                Collection, It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<KeyValuePair<string, string>[]>()))
            .Callback<string, string, float[], KeyValuePair<string, string>[]>((_, id, _, payload) => written.Add((id, payload)))
            .Returns(Task.CompletedTask);

        var result = await Create().AddArticle(
            new AddArticleTextRequest { Title = "公司法", Content = "第 1 條\n內文一" });

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("公司法", json.GetProperty("title").GetString());
        Assert.Equal(2, json.GetProperty("articleCount").GetInt32());
        Assert.Equal(2, json.GetProperty("chunkCount").GetInt32());
        Assert.Equal(3, json.GetProperty("replacedCount").GetInt32());

        _parser.Verify(p => p.ParseTextAsync(
            "第 1 條\n內文一", "公司法", "(text)", It.IsAny<CancellationToken>()), Times.Once);
        _qdrant.Verify(q => q.InitializeCollectionAsync(Collection), Times.Once);

        Assert.Equal(new[] { "id-1", "id-2" }, written.Select(w => w.Id));
        var payload = written[0].Payload.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("公司法", payload["title"]);
        Assert.Equal("第 1 條\n內文一", payload["content"]);
        Assert.Equal("總則", payload["chapter"]);
        Assert.Equal("第 1 條", payload["articleNumber"]);
        Assert.Equal("8", payload["nChars"]);
        Assert.Equal("(text)", payload["sourceFile"]);
    }

    [Fact]
    public async Task AddArticle_沒要求預熱就不預熱_要求了就用片段全文預熱()
    {
        ParserReturns(TwoChunks());
        var controller = Create();

        var noWarmupResult = await controller.AddArticle(
            new AddArticleTextRequest { Title = "公司法", Content = "x", Warmup = false });
        _warmup.VerifyNoOtherCalls();
        var noWarmupJson = TestFactory.ToJson(Assert.IsType<OkObjectResult>(noWarmupResult).Value);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, noWarmupJson.GetProperty("warmup").ValueKind);

        var warmupResult = await controller.AddArticle(
            new AddArticleTextRequest { Title = "公司法", Content = "x", Warmup = true });

        _warmup.Verify(w => w.WarmupAsync(
            It.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "第 1 條\n內文一", "第 2 條\n內文二" })),
            null,
            It.IsAny<CancellationToken>()), Times.Once);
        var withJson = TestFactory.ToJson(Assert.IsType<OkObjectResult>(warmupResult).Value);
        // warmup 是 WarmupProgress 物件本身，測試用的預設序列化不轉 camelCase，欄位名稱維持 PascalCase
        // （實際 API 由 ASP.NET 序列化，輸出是 camelCase）
        Assert.Equal(2, withJson.GetProperty("warmup").GetProperty("ProcessedChunks").GetInt32());
    }

    // ════════════════════════════════════════════════════════════
    //  預熱
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Warmup_沒帶條件時預熱全部片段_略過空白內容()
    {
        CorpusIs(Chunk("1", "公司法", "總則", "內文一"),
                 Chunk("2", "民法", "通則", "內文二"),
                 Chunk("3", "民法", "通則", "   "));

        var result = await Create().Warmup(null);

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, json.GetProperty("totalChunks").GetInt32());
        Assert.Equal(100, json.GetProperty("totalPromptTokens").GetInt64());
        Assert.Contains("重新執行", json.GetProperty("note").GetString());
        _warmup.Verify(w => w.WarmupAsync(
            It.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "內文一", "內文二" })),
            null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Warmup_依法典與上限篩選()
    {
        CorpusIs(Chunk("1", "民法", "通則", "甲"),
                 Chunk("2", "民法", "通則", "乙"),
                 Chunk("3", "公司法", "總則", "丙"));

        await Create().Warmup(new WarmupRequest { Law = "民法", Limit = 1 });

        _warmup.Verify(w => w.WarmupAsync(
            It.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "甲" })),
            null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Warmup_上限為零或負數視為不限()
    {
        CorpusIs(Chunk("1", "民法", "通則", "甲"), Chunk("2", "民法", "通則", "乙"));

        await Create().Warmup(new WarmupRequest { Limit = 0 });

        _warmup.Verify(w => w.WarmupAsync(
            It.Is<IReadOnlyList<string>>(l => l.Count == 2), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Warmup_沒有符合的片段回_400_並列出可用法典()
    {
        CorpusIs(Chunk("1", "民法", "通則", "甲"));

        var result = await Create().Warmup(new WarmupRequest { Law = "刑法" });

        var json = TestFactory.ToJson(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Equal("民法", json.GetProperty("availableLaws")[0].GetString());
        _warmup.VerifyNoOtherCalls();
    }

    // ════════════════════════════════════════════════════════════
    //  知識庫現況
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetDocuments_統計每部法典並標示預熱狀態()
    {
        CorpusIs(Chunk("1", "公司法", "總則", "aaa"),      // 3 字
                 Chunk("2", "公司法", "總則", "b"),        // 1 字
                 Chunk("3", "公司法", "罰則", "cc"),       // 2 字
                 Chunk("4", "民法", "通則", "dddd"));
        _tracker.Setup(t => t.IsLikelyCached("aaa")).Returns(true);
        _tracker.Setup(t => t.IsLikelyCached("b")).Returns(true);
        _tracker.Setup(t => t.IsLikelyCached("cc")).Returns(true);

        var result = await Create().GetDocuments();

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(4, json.GetProperty("totalChunks").GetInt32());
        Assert.Equal(3, json.GetProperty("warmedChunks").GetInt32());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("warmupNote").GetString()));

        var laws = json.GetProperty("laws").EnumerateArray()
            .ToDictionary(l => l.GetProperty("law").GetString()!);

        var company = laws["公司法"];
        Assert.Equal(3, company.GetProperty("chunks").GetInt32());
        Assert.Equal(6, company.GetProperty("totalChars").GetInt32());
        Assert.Equal(2, company.GetProperty("medianChars").GetInt32());   // 排序後 [1,2,3] 的中間值
        Assert.Equal(2, company.GetProperty("chapters").GetInt32());
        Assert.Equal(3, company.GetProperty("warmedChunks").GetInt32());
        Assert.True(company.GetProperty("warmed").GetBoolean());

        var civil = laws["民法"];
        Assert.Equal(0, civil.GetProperty("warmedChunks").GetInt32());
        Assert.False(civil.GetProperty("warmed").GetBoolean());
    }

    [Fact]
    public async Task GetDocuments_片段清單可依法典篩選_統計不受影響()
    {
        CorpusIs(Chunk("1", "公司法", "總則", "甲"), Chunk("2", "民法", "通則", "乙"));

        var result = await Create().GetDocuments(law: "民法");

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, json.GetProperty("laws").GetArrayLength());
        var chunks = json.GetProperty("chunks");
        Assert.Equal(1, chunks.GetArrayLength());
        Assert.Equal("民法", chunks[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task GetDocuments_空知識庫不丟例外()
    {
        CorpusIs();

        var result = await Create().GetDocuments();

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(0, json.GetProperty("totalChunks").GetInt32());
        Assert.Equal(0, json.GetProperty("laws").GetArrayLength());
    }

    // ════════════════════════════════════════════════════════════
    //  搜尋與刪除
    // ════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Search_沒有問題回_400(string query)
    {
        var result = await Create().Search(query);

        Assert.IsType<BadRequestObjectResult>(result);
        _embedding.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Search_向量化後檢索並回傳分數()
    {
        _qdrant
            .Setup(q => q.SearchSimilarAsync(Collection, It.Is<float[]>(v => v[0] == 0.5f), 2))
            .ReturnsAsync(new List<(KnowledgeSearchResult Result, double Score)>
            {
                (Chunk("1", "公司法", "總則", "內文"), 0.91),
            });

        var result = await Create().Search("股東人數", topK: 2);

        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("股東人數", json.GetProperty("query").GetString());
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        var hit = json.GetProperty("results")[0];
        Assert.Equal("公司法", hit.GetProperty("Title").GetString());
        Assert.Equal(0.91, hit.GetProperty("score").GetDouble());
    }

    [Fact]
    public async Task DeleteLaw_刪到零筆回_404_有刪到回筆數()
    {
        var controller = Create();
        _qdrant.Setup(q => q.DeleteByTitleAsync(Collection, "刑法", It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _qdrant.Setup(q => q.DeleteByTitleAsync(Collection, "民法", It.IsAny<CancellationToken>())).ReturnsAsync(7);

        Assert.IsType<NotFoundObjectResult>(await controller.DeleteLaw("刑法"));

        var ok = await controller.DeleteLaw("民法");
        var json = TestFactory.ToJson(Assert.IsType<OkObjectResult>(ok).Value);
        Assert.Equal(7, json.GetProperty("deleted").GetInt32());
    }

    [Fact]
    public async Task DeleteDocument_刪除指定片段()
    {
        var result = await Create().DeleteDocument("id-9");

        Assert.IsType<OkObjectResult>(result);
        _qdrant.Verify(q => q.DeleteDocumentAsync(Collection, "id-9"), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("no")]
    [InlineData("YES")]       // 大小寫要完全相符，破壞性操作寧可嚴格
    public async Task Clear_沒有確認就不清空(string? confirm)
    {
        var result = await Create().Clear(confirm);

        Assert.IsType<BadRequestObjectResult>(result);
        _qdrant.Verify(q => q.TruncateCollectionAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Clear_帶_confirm_yes_才清空()
    {
        var result = await Create().Clear("yes");

        Assert.IsType<OkObjectResult>(result);
        _qdrant.Verify(q => q.TruncateCollectionAsync(Collection), Times.Once);
    }
}
