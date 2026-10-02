namespace ChatBot.Api.Models;

/// <summary>一個聊天室。</summary>
public sealed class Conversation
{
    public Guid Id { get; set; }

    /// <summary>標題。建立時若未指定，會取第一則使用者訊息的前 30 字。</summary>
    public string Title { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>訊息則數，僅在清單查詢時填入。</summary>
    public int MessageCount { get; set; }
}

/// <summary>
/// 聊天室裡的一則訊息。
///
/// assistant 訊息同時保存當次的量測數據——TTFT、命中率、快取模式、片段排列。
/// 把這些存進資料庫而不是只回給前端，是為了讓「三種快取模式的差異」
/// 事後可以直接用 SQL 查，不必另外做紀錄：
///
///   SELECT cache_mode, count(*), avg(ttft_ms), avg(cache_hit_rate)
///   FROM messages WHERE role = 'assistant' GROUP BY cache_mode;
/// </summary>
public sealed class ConversationMessage
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }

    /// <summary>"user" 或 "assistant"。</summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// 訊息內容。assistant 的訊息存的是**模型原始輸出**，
    /// 不含「📜 法律諮詢回覆」標題與免責聲明——那兩段是呈現用的裝飾，
    /// 存進來會在下一輪被當成對話歷史送回模型，白白浪費 token 也擾亂上下文。
    /// </summary>
    public string Content { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    // ── 以下僅 assistant 訊息有值 ───────────────────────────────
    public string? CacheMode { get; set; }
    public string? ChunkOrder { get; set; }
    public int? TtftMs { get; set; }
    public int? TotalMs { get; set; }
    public int? PromptTokens { get; set; }
    public int? CachedTokens { get; set; }
    public double? CacheHitRate { get; set; }
    public int? PrefixCeilingTokens { get; set; }
    public double? CachedOverPrefixCeiling { get; set; }
    public List<string>? RetrievedArticles { get; set; }
}

// ── API 的請求／回應型別 ────────────────────────────────────────

public sealed class CreateConversationRequest
{
    /// <summary>可省略，之後由第一則訊息自動命名。</summary>
    public string? Title { get; set; }
}

public sealed class RenameConversationRequest
{
    public required string Title { get; set; }
}

public sealed class ConversationDetail
{
    public required Conversation Conversation { get; set; }
    public List<ConversationMessage> Messages { get; set; } = new();
}
