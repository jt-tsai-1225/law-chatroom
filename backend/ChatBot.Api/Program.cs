using ChatBot.Api.Services;
using Qdrant.Client;
using ChatBot.Api.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS policy
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(["http://localhost:5173", "http://localhost:80"])
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Register ChatBot service
builder.Services.AddScoped<IChatBotService, ChatBotService>();

// Register RAG services
builder.Services.AddScoped<IQdrantService, QdrantService>();

// 法規解析。以子行程呼叫 tools/parse_pdf.py——那份 Python 實作是唯一
// 經過實測的切分邏輯（327 片段、0 重複標籤），在 C# 重寫等於重新承擔
// 一次靜默切壞的風險，見 LegalDocumentParser 的說明。
builder.Services.AddScoped<ILegalDocumentParser, LegalDocumentParser>();

// 原始檔儲存。由設定決定走本機磁碟或 SeaweedFS 的 S3 閘道——
// 上傳流程只認 IObjectStorage，換後端不影響解析、向量化與預熱。
// 直接讀設定值而非用已繫結的物件：RAGSettings 的繫結在本檔案下方才發生，
// 而這裡要在註冊服務時就決定用哪一個實作。
// 環境變數 RAGSettings__ObjectStorageBackend 會對應到這個鍵。
var storageBackend =
    (builder.Configuration["RAGSettings:ObjectStorageBackend"] ?? "local").ToLowerInvariant();

if (storageBackend is "seaweedfs" or "s3")
{
    builder.Services.AddSingleton<IObjectStorage, SeaweedObjectStorage>();
}
else
{
    builder.Services.AddSingleton<IObjectStorage, LocalObjectStorage>();
}

// 匯入流程。JobStore 必須是 Singleton：上傳端點立刻回應，
// 實際工作在背景跑，進度要跨請求查得到。
builder.Services.AddSingleton<IIngestionJobStore, IngestionJobStore>();
builder.Services.AddScoped<IIngestionService, IngestionService>();
builder.Services.AddScoped<IKvWarmupService, KvWarmupService>();
builder.Services.AddHttpClient("Embedding", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<IEmbeddingService, EmbeddingService>();

// 快取模式 → vLLM 端點的對應與可用性探測。
// Singleton：探測結果要跨請求共用，不然每次開頁面都打三次健康檢查。
builder.Services.AddSingleton<ILlmEndpointRegistry, LlmEndpointRegistry>();

// Register LLM service for RAG response generation
builder.Services.AddScoped<ILLMService, LLMService>();

// 聊天室紀錄。Singleton：內部持有 NpgsqlDataSource（連線池），
// 每個請求各建一個會把連線池的意義抵銷掉。
builder.Services.AddSingleton<IConversationStore, ConversationStore>();

// Tokenizer 與 prompt 組裝（CacheBlend 需要以 token id 送出請求）
// TokenizerClient 為 Singleton：它對固定文字（系統提示詞、分隔符、條文片段）
// 做記憶體快取，跨請求共用才有意義。
builder.Services.AddSingleton<ITokenizerClient, TokenizerClient>();
builder.Services.AddScoped<IPromptBuilder, PromptBuilder>();

// 記錄哪些片段送過，用來把已快取的片段排到前面（Singleton：跨請求共用才有意義）
builder.Services.AddSingleton<IChunkCacheTracker, ChunkCacheTracker>();

// Register LMcache service
builder.Services.AddSingleton<LMCacheService>();
builder.Services.AddScoped<ILMCacheService>(sp => sp.GetRequiredService<LMCacheService>());

// Register RAG settings from configuration
builder.Services.Configure<RAGSettings>(builder.Configuration.GetSection("RAGSettings"));

// Configure Qdrant client
// Qdrant gRPC 使用端口 6334，HTTP API 使用端口 6333
// .NET Client (QueryAsync) 使用 gRPC 連接，所以需要使用 6334
var ragSettings = builder.Configuration.GetSection("RAGSettings").Get<RAGSettings>();

// 從 Configuration 讀取 gRPC 端口（支援環境變數 QDRANT_GRPC_PORT）
var grpcPortStr = builder.Configuration.GetValue<string>("QDRANT_GRPC_PORT") ??
                  ragSettings?.QdrantGrpcPort.ToString() ?? "6334";
var grpcPort = int.Parse(grpcPortStr);

builder.Services.AddSingleton(sp => new QdrantClient(
    host: ragSettings?.QdrantHost ?? "qdrant",
    port: grpcPort));

var app = builder.Build();

// 聊天室資料表。冪等的 CREATE TABLE IF NOT EXISTS，重複執行無害。
//
// 失敗不讓服務起不來：資料庫是聊天紀錄用的，問答本身不依賴它。
// 因為這個失敗而讓整個後端無法啟動，等於把次要功能的故障
// 升級成主要功能的故障。聊天室端點會各自回 503 說明狀況。
using (var scope = app.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<IConversationStore>();
    var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                   .CreateLogger("Startup");
    try
    {
        await store.EnsureSchemaAsync();
    }
    catch (Exception ex)
    {
        log.LogError(ex,
            "聊天室資料表建立失敗，聊天紀錄功能將不可用。問答不受影響");
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
app.UseAuthorization();
app.MapControllers();

app.Run();
