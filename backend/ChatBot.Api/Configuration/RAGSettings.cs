namespace ChatBot.Api.Configuration;

public class RAGSettings
{
    public const string SectionName = "RAGSettings";

    public string QdrantHost { get; set; } = "localhost";
    public int QdrantPort { get; set; } = 6333;  // HTTP API 端口
    public int QdrantGrpcPort { get; set; } = 6334;  // gRPC 端口 (用於 .NET Client QueryAsync)
    public string QdrantApiKey { get; set; } = "";

    // Embedding 配置 - 預設使用 Qwen3-Embedding-0.6B
    public string EmbeddingApiKey { get; set; } = "";
    public string EmbeddingEndpoint { get; set; } = "https://ainexus.phison.com/api/external/v1/embeddings";
    public string EmbeddingModel { get; set; } = "Qwen/Qwen3-Embedding-0.6B";
    public int EmbeddingDimensions { get; set; } = 1024;  // Qwen3-Embedding-0.6B 使用 1024 維向量

    // ── 推論引擎（自架 vLLM + LMCache）─────────────────────────────
    //
    // 改為自架的原因：CacheBlend 是推論引擎內部的 KV 快取機制，
    // 需要 LMCache 與特定啟動參數，外部 gateway 無法提供。
    //
    // 對應的 vLLM 啟動參數（缺一不可）：
    //   --kv-transfer-config '{"kv_connector":"LMCacheConnectorV1","kv_role":"kv_both"}'
    //   --no-enable-prefix-caching        必須明確關閉，省略不等於關閉
    //   --enable-prompt-tokens-details    否則 usage 不會回報 cached_tokens
    //   --max-num-batched-tokens 32768    必須大於最長 prompt
    //   --enforce-eager                   CacheBlend 不支援 CUDA Graph

    /// <summary>推論引擎的根位址；/tokenize 與 /v1/completions 都由此組出。</summary>
    public string LlmBaseUrl { get; set; } = "http://10.102.197.193:8000";

    /// <summary>自架服務通常不需要，留空即不送 Authorization 標頭。</summary>
    public string LlmApiKey { get; set; } = "";

    public string LlmModel { get; set; } = "mistralai/Mistral-7B-Instruct-v0.2";
    public double LlmTemperature { get; set; } = 0.2;
    public int LlmMaxTokens { get; set; } = 1024;
    public int LlmTimeoutSeconds { get; set; } = 300;

    // ── CacheBlend 的 prompt 組裝參數 ──────────────────────────────

    /// <summary>
    /// 片段分隔符。必須與 vLLM 容器的 LMCACHE_BLEND_SPECIAL_STR 完全一致，
    /// 否則引擎切不出片段邊界。注意 LMCache 會 strip 前後空白。
    /// 本模型下 "# #" 的 token id 為 [422, 422]。
    /// </summary>
    public string BlendSeparator { get; set; } = "# #";

    /// <summary>
    /// 對應 LMCache 的 blend_min_tokens（預設 256）。短於此值的片段不會走 blend，
    /// 僅用於在組 prompt 時發出警告，提醒匯入的分組顆粒度過細。
    /// </summary>
    public int BlendMinTokens { get; set; } = 256;

    /// <summary>Mistral-7B-Instruct 的 BOS + [INST]。換模型時必須一併更換。</summary>
    public int[] InstPrefixTokens { get; set; } = { 1, 733, 16289, 28793 };

    /// <summary>Mistral-7B-Instruct 的 [/INST]。</summary>
    public int[] InstSuffixTokens { get; set; } = { 733, 28748, 16289, 28793 };

    /// <summary>
    /// 系統提示詞。這段文字會成為 prompt 的第一個片段，內容一改快取就全部失效，
    /// 因此不要在其中放入日期、使用者名稱之類每次都會變動的東西。
    /// </summary>
    public string SystemPrompt { get; set; } =
        "你是一位專業的法律諮詢助理，專門回答中華民國民法與公司法的問題。" +
        "請只根據以下提供的法律條文回答問題，並明確指出依據的條號。" +
        "若提供的條文不足以回答，請直接說明資料不足，不要自行補充條文內容。" +
        "請使用繁體中文，回答力求精確、有條理。";

    // ── 檢索 ───────────────────────────────────────────────────────

    /// <summary>每次檢索取回的條文片段數。</summary>
    public int RetrievalTopK { get; set; } = 5;

    // ── C# 層的回答快取 ────────────────────────────────────────────
    //
    // ⚠ 預設關閉，而且量測 CacheBlend 效果時務必保持關閉。
    //
    // 這一層以 prompt 的雜湊為鍵、直接回傳先前的回答字串，命中時
    // 根本不會送到推論引擎，TTFT 會記成 0。那個 0 與 CacheBlend
    // 省下的時間毫無關係，開著會讓效能數據完全失真。
    //
    // 它與 LMCache 沒有任何關係，只是命名相近；LMCache 快取的是
    // 模型內部的 KV，這一層快取的是輸出字串。
    public bool LmcacheEnabled { get; set; } = false;
    public int LmcacheMaxEntries { get; set; } = 1000;
    public int LmcacheDefaultTtlMinutes { get; set; } = 60;
    public string LmcacheStorageType { get; set; } = "memory";
}
