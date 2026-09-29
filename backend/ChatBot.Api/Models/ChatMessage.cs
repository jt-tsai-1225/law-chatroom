namespace ChatBot.Api.Models;

public class ChatMessage
{
    public required string Role { get; set; }
    public required string Content { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class ChatRequest
{
    public required string Message { get; set; }
    public List<ChatMessage>? ConversationHistory { get; set; }

    /// <summary>
    /// 這一次要如何排列檢索回來的條文片段。用於驗證 CacheBlend 的非前綴複用能力。
    ///
    ///   null / "default"  依快取狀態重排（正式行為，見 RAGSettings.ReorderByCacheStatus）
    ///   "asis"            維持檢索的相關度順序，不做任何重排
    ///   "reverse"         把檢索順序完全倒轉
    ///
    /// 為什麼需要這個開關：
    ///   要驗證「同一組片段換個順序，快取是否仍然命中」，必須讓片段組合不變、
    ///   只變順序。但正常問答做不到——換一個問題就會檢索到另一批片段，
    ///   問同一個問題順序又永遠一樣。因此把順序拉成單次請求的參數，
    ///   其餘流程（embedding、檢索、去重、組 prompt、送出）完全不變。
    /// </summary>
    public string? ChunkOrder { get; set; }
}

public class ChatResponse
{
    public required string Reply { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    // TTFT Benchmark Metrics (optional, may be 0 if not measured)
    public long TtftMilliseconds { get; set; }
    public long TotalMilliseconds { get; set; }
    public int TokenCount { get; set; }
    public double TokensPerSecond { get; set; }

    /// <summary>由 C# 層的回答快取直接回傳。為 true 時 TTFT 不具參考價值。</summary>
    public bool FromCache { get; set; }

    // ── CacheBlend 命中率（由推論引擎的 usage 回報）────────────────
    /// <summary>送入模型的 prompt 長度。</summary>
    public int PromptTokens { get; set; }

    /// <summary>KV 快取命中的 token 數。接近 PromptTokens 表示 blend 有生效。</summary>
    public int CachedTokens { get; set; }

    /// <summary>CachedTokens / PromptTokens，百分比。</summary>
    public double CacheHitRate { get; set; }

    /// <summary>本次回答實際送入模型的條文片段，順序即 prompt 中的順序。</summary>
    public List<string> RetrievedArticles { get; set; } = new();

    /// <summary>本次是否依快取狀態重排過片段順序（見 RAGSettings.ReorderByCacheStatus）。</summary>
    public bool ReorderedForCache { get; set; }

    /// <summary>本次實際採用的排列方式："default" / "asis" / "reverse"。</summary>
    public string ChunkOrderApplied { get; set; } = "default";
}
