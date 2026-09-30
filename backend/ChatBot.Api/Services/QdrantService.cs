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

    /// <summary>
    /// 刪除某一部法典的全部片段，回傳刪除的數量。
    ///
    /// 重新上傳同名法典時用它做「取代」：切分規則一改，若不先清掉舊片段，
    /// 兩種顆粒度的內容會並存於同一個集合、檢索同時撈到兩種——
    /// 那是最難察覺的一種髒資料。
    /// </summary>
    Task<int> DeleteByTitleAsync(
        string collectionName, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取回集合中的所有片段（不做向量搜尋）。供診斷端點指定片段與順序使用。
    /// 目前語料僅 327 筆，全部載入的成本可忽略，因此不做分頁與過濾。
    /// </summary>
    Task<List<KnowledgeSearchResult>> ScrollAllAsync(
        string collectionName = "legal_documents", int limit = 2000);
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

        // 建立 payload 索引。
        // ⚠ 欄位名稱必須與寫入時的 payload 鍵**逐字相同**（大小寫有別）。
        // 先前這裡寫的是 "ArticleNumber" 與 "Chapter"，而實際寫入的鍵是
        // "articleNumber" 與 "chapter"——索引因此建在不存在的欄位上，
        // 不會報錯，只是完全沒有作用。
        foreach (var field in new[] { "title", "articleNumber", "chapter", "sourceFile" })
        {
            await _client.CreatePayloadIndexAsync(
                collectionName, field, schemaType: PayloadSchemaType.Keyword);
        }
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
                Id = ExtractPointId(r.Id),
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

    /// <summary>
    /// 取出乾淨的 point id 字串。
    ///
    /// 不能用 PointId.ToString()：那是 protobuf 產生的方法，回傳的是
    /// JSON 表示（例如 {"uuid":"…"}），不是 id 本身。拿它去比對或刪除都會失敗。
    ///
    /// PointId 是 oneof，未設定的那一邊會回傳型別預設值，
    /// 因此以「Uuid 是否為空」判斷即可，不需要依賴產生的列舉名稱。
    /// </summary>
    private static string ExtractPointId(Qdrant.Client.Grpc.PointId id)
        => !string.IsNullOrEmpty(id.Uuid) ? id.Uuid : id.Num.ToString();

    private string ExtractPayloadValue(IDictionary<string, Qdrant.Client.Grpc.Value> payload, string key)
    {
        if (!payload.TryGetValue(key, out var payloadValue) || payloadValue is null)
            return string.Empty;

        return payloadValue.StringValue ?? string.Empty;
    }

    public async Task<List<KnowledgeSearchResult>> ScrollAllAsync(
        string collectionName = "legal_documents", int limit = 2000)
    {
        var response = await _client.ScrollAsync(
            collectionName: collectionName,
            limit: (uint)limit);

        var list = response.Result
            .Select(p => new KnowledgeSearchResult
            {
                Id = ExtractPointId(p.Id),
                Content = ExtractPayloadValue(p.Payload, "content"),
                Title = ExtractPayloadValue(p.Payload, "title"),
                Chapter = ExtractPayloadValue(p.Payload, "chapter"),
                ArticleNumber = ExtractPayloadValue(p.Payload, "articleNumber"),
                Score = 0
            })
            .ToList();

        _logger.LogInformation("[QdrantService] ScrollAll 取回 {Count} 個片段", list.Count);
        return list;
    }

    public async Task DeleteDocumentAsync(string collectionName, string documentId)
    {
        await _client.DeleteAsync(collectionName, new Guid(documentId));
    }

    public async Task<int> DeleteByTitleAsync(
        string collectionName, string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return 0;

        if (!await CollectionExistsAsync(collectionName)) return 0;

        var all = await ScrollAllAsync(collectionName);
        var victims = all
            .Where(c => string.Equals(c.Title, title, StringComparison.Ordinal))
            .ToList();

        if (victims.Count == 0) return 0;

        // 這個集合同時存在兩種 id 型別：早期以腳本匯入的片段用整數 id，
        // 經由本服務寫入的用 UUID。客戶端的刪除方法依型別分開，
        // 因此先分組再各自刪除。
        var guids = new List<Guid>();
        var nums = new List<ulong>();

        foreach (var v in victims)
        {
            if (Guid.TryParse(v.Id, out var g)) guids.Add(g);
            else if (ulong.TryParse(v.Id, out var n)) nums.Add(n);
            else _logger.LogWarning("無法辨識的 point id，略過刪除：{Id}", v.Id);
        }

        if (guids.Count > 0) await _client.DeleteAsync(collectionName, guids);
        if (nums.Count > 0) await _client.DeleteAsync(collectionName, nums);

        var deleted = guids.Count + nums.Count;
        _logger.LogInformation(
            "[QdrantService] 已刪除「{Title}」的 {Count} 個片段（UUID {G}、整數 {N}）",
            title, deleted, guids.Count, nums.Count);

        return deleted;
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
