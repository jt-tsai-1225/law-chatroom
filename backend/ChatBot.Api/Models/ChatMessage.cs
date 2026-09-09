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
    public bool FromCache { get; set; }
}
