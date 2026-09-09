namespace ChatBot.Api.Configuration;

public class RAGSettings
{
    public const string SectionName = "RAG";

    public string QdrantHost { get; set; } = "localhost";
    public int QdrantPort { get; set; } = 6333;  // HTTP API 端口
    public int QdrantGrpcPort { get; set; } = 6334;  // gRPC 端口 (用於 .NET Client QueryAsync)
    public string QdrantApiKey { get; set; } = "";
    
    // Embedding 配置 - 預設使用 Qwen3-Embedding-0.6B
    public string EmbeddingApiKey { get; set; } = "";
    public string EmbeddingEndpoint { get; set; } = "https://ainexus.phison.com/api/external/v1/embeddings";
    public string EmbeddingModel { get; set; } = "Qwen/Qwen3-Embedding-0.6B";
    public int EmbeddingDimensions { get; set; } = 1024;  // Qwen3-Embedding-0.6B 使用 1024 維向量
    
    // LLM Chat 配置 - 用於 RAG 回覆生成
    public string LlmApiKey { get; set; } = "";
    public string LlmEndpoint { get; set; } = "https://ainexus.phison.com/api/external/v1/chat/completions";
    public string LlmModel { get; set; } = "Qwen/Qwen3.6-35B-A3B-FP8";
    public double LlmTemperature { get; set; } = 0.2;  // 較低溫度以獲得更精確的法律回覆
    public int LlmMaxTokens { get; set; } = 2000;  // 最大回覆長度
    
    // LMcache 配置
    public bool LmcacheEnabled { get; set; } = true;
    public int LmcacheMaxEntries { get; set; } = 1000;
    public int LmcacheDefaultTtlMinutes { get; set; } = 60;
    public string LmcacheStorageType { get; set; } = "memory";
    
    // CacheBlend 配置
    public bool CacheBlendEnabled { get; set; } = true;
    public string CacheBlendStrategy { get; set; } = "similarity";
    public double CacheBlendSimilarityThreshold { get; set; } = 0.3;
    public int CacheBlendTopK { get; set; } = 3;
    public double CacheBlendWeight { get; set; } = 0.7;
}
