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
builder.Services.AddHttpClient("LLM", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddScoped<ILLMService, LLMService>();

// Register LMcache service
builder.Services.AddSingleton<LMCacheService>();
builder.Services.AddScoped<ILMCacheService>(sp => sp.GetRequiredService<LMCacheService>());

// Register CacheBlend service
builder.Services.AddSingleton<CacheBlendService>();
builder.Services.AddScoped<ICacheBlendService>(sp => sp.GetRequiredService<CacheBlendService>());

// Register RAG settings from configuration
builder.Services.Configure<RAGSettings>(builder.Configuration.GetSection("RAGSettings"));

// Configure Qdrant client
// Qdrant gRPC 使用端口 6334，HTTP API 使用端口 6333
// .NET Client (QueryAsync) 使用 gRPC 連接，所以需要使用 6334
// 所有設定皆可用 RAGSettings__QdrantHost / RAGSettings__QdrantGrpcPort 等環境變數覆寫
var ragSettings = builder.Configuration.GetSection("RAGSettings").Get<RAGSettings>();

builder.Services.AddSingleton(sp => new QdrantClient(
    host: ragSettings?.QdrantHost ?? "qdrant",
    port: ragSettings?.QdrantGrpcPort ?? 6334));

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
