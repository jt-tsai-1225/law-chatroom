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
}
