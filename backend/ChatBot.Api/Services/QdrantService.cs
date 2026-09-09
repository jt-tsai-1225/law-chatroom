using ChatBot.Api.Configuration;
using ChatBot.Api.Models;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace ChatBot.Api.Services;

/// <summary>
/// Qdrant 向量資料庫服務
/// </summary>
public interface IQdrantService
{
    Task InitializeCollectionAsync(string collectionName = "legal_documents");
    Task UpsertDocumentAsync(string collectionName, string documentId, float[] vector,
        KeyValuePair<string, string>[] payload);
    Task<List<(KnowledgeSearchResult Result, double Score)>> SearchSimilarAsync(
        string collectionName, float[] queryVector, int topK = 5);
    Task DeleteDocumentAsync(string collectionName, string documentId);
    Task<bool> CollectionExistsAsync(string collectionName);
    Task CreateCollectionAsync(string collectionName, int vectorSize = 1536);
    Task TruncateCollectionAsync(string collectionName);
}

public class QdrantService : IQdrantService
{
    private readonly QdrantClient _client;
    private readonly RAGSettings _settings;
    private readonly ILogger<QdrantService> _logger;
    private const string DefaultCollectionName = "legal_documents";

    public QdrantService(QdrantClient client, IOptions<RAGSettings> settings, ILogger<QdrantService> logger)
    {
        _client = client;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task InitializeCollectionAsync(string collectionName = "legal_documents")
    {
        if (!await CollectionExistsAsync(collectionName))
        {
            await CreateCollectionAsync(collectionName, _settings.EmbeddingDimensions);
        }
    }

    public async Task CreateCollectionAsync(string collectionName, int vectorSize = 1024)
    {
        // Qwen3-Embedding-0.6B 使用 1024 維向量
        await _client.CreateCollectionAsync(
            collectionName,
            new VectorParams
            {
                Size = (ulong)vectorSize,
                Distance = Distance.Cosine
            });

        // 建立權重索引
        await _client.CreatePayloadIndexAsync(
        collectionName,
        "ArticleNumber",
        schemaType: PayloadSchemaType.Keyword);
        await _client.CreatePayloadIndexAsync(
        collectionName,
        "Chapter",
        schemaType: PayloadSchemaType.Keyword);
    }

    public async Task<bool> CollectionExistsAsync(string collectionName)
    {
        var collections = await _client.ListCollectionsAsync();
        return collections.Any(c => c == collectionName);
    }

    public async Task UpsertDocumentAsync(string collectionName, string documentId, float[] vector,
            KeyValuePair<string, string>[] payload)
        {
            var point = new PointStruct
            {
                Id = new PointId { Uuid = documentId },
                Vectors = vector
            };

        foreach (var p in payload)
        {
            point.Payload[p.Key] = p.Value;
        }

        await _client.UpsertAsync(collectionName, new[] { point });
    }

    /// <summary>
    /// 使用 Qdrant Search API 搜尋相似向量
    /// 使用 Qdrant.Client 1.9.0 的 SearchAsync 方法 (相容於 Qdrant 伺服器 1.19.0)
    /// </summary>
    public async Task<List<(KnowledgeSearchResult Result, double Score)>> SearchSimilarAsync(
        string collectionName, float[] queryVector, int topK = 5)
    {
        _logger.LogInformation("[QdrantService] 開始搜尋 - collection: {CollectionName}, topK: {TopK}", collectionName, topK);
        
        // 使用 SearchAsync (Qdrant.Client 1.9.0 支援的方法)
        var results = await _client.SearchAsync(
            collectionName: collectionName,
            vector: queryVector,
            limit: (ulong)topK
        );

        var resultList = results.ToList();
        _logger.LogInformation("[QdrantService] 搜尋完成 - 獲得 {ResultCount} 個結果", resultList.Count);
        
        foreach (var r in resultList)
        {
            _logger.LogInformation("[QdrantService] 結果 - Id: {Id}, Score: {Score}", r.Id, r.Score);
        }

        return resultList.Select(r => (
            Result: new KnowledgeSearchResult
            {
                Id = r.Id.ToString(),
                // 從 Qdrant payload 中提取實際值（處理 JSON 格式）
                Content = ExtractPayloadValue(r.Payload, "content"),
                Title = ExtractPayloadValue(r.Payload, "title"),
                Chapter = ExtractPayloadValue(r.Payload, "chapter"),
                ArticleNumber = ExtractPayloadValue(r.Payload, "articleNumber"),
                Score = (double)r.Score
            },
            Score: (double)r.Score
        )).ToList();
    }

    private string ExtractPayloadValue(IDictionary<string, Qdrant.Client.Grpc.Value> payload, string key)
    {
        if (!payload.TryGetValue(key, out var payloadValue) || payloadValue is null)
            return string.Empty;

        return payloadValue.StringValue ?? string.Empty;
    }

    public async Task DeleteDocumentAsync(string collectionName, string documentId)
    {
        await _client.DeleteAsync(collectionName, new Guid(documentId));
    }

    /// <summary>
    /// 清空集合中的所有數據
    /// </summary>
    public async Task TruncateCollectionAsync(string collectionName)
    {
        // 刪除並重新創建集合來清空數據
        await _client.DeleteCollectionAsync(collectionName);
        await CreateCollectionAsync(collectionName, _settings.EmbeddingDimensions);
    }
}
