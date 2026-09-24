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
builder.Services.AddScoped<IPDFParser, PDFParser>();
builder.Services.AddHttpClient("Embedding", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<IEmbeddingService, EmbeddingService>();

// Register LLM service for RAG response generation
builder.Services.AddScoped<ILLMService, LLMService>();

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
