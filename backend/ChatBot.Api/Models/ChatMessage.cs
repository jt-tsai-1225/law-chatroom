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
    ///   "relevance"       維持檢索的相關度順序，不做任何重排（基準線）
    ///   "swap-tail"       只對調最後兩段
    ///   "swap-head"       只對調前兩段
    ///   "reverse"         整個倒轉
    ///   "shuffle"         完全打亂（由 ChunkOrderSeed 決定，可重現）
    ///
    /// "asis" 是 "relevance" 的舊名，仍可使用；回應一律回報正式名稱。
    /// 無法辨識的值會退回 "default"，不會使請求失敗。
    ///
    /// 為什麼需要這個開關：
    ///   要驗證「同一組片段換個順序，快取是否仍然命中」，必須讓片段組合不變、
    ///   只變順序。但正常問答做不到——換一個問題就會檢索到另一批片段，
    ///   問同一個問題順序又永遠一樣。因此把順序拉成單次請求的參數，
    ///   其餘流程（embedding、檢索、去重、組 prompt、送出）完全不變。
    ///
    /// 各模式為何這樣設計，見 ChunkOrderStrategy 的註解——重點是
    /// swap-tail 與 swap-head 的對比，兩者的**前綴快取理論上限差距極大**，
    /// 可用來判斷高命中率究竟來自 blend 還是單純的前綴複用。
    /// </summary>
    public string? ChunkOrder { get; set; }

    /// <summary>
    /// shuffle 模式的亂數種子。省略時使用 ChunkOrderStrategy.DefaultSeed。
    /// 洗牌演算法規格固定（自帶 xorshift32，不依賴 System.Random），
    /// 因此同一個 seed 在任何執行環境、任何 .NET 版本都會得到相同排列。
    /// </summary>
    public int? ChunkOrderSeed { get; set; }
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

    /// <summary>
    /// 本次實際採用的排列方式，一律為正式名稱：
    /// "default" / "relevance" / "swap-tail" / "swap-head" / "reverse" / "shuffle"。
    /// </summary>
    public string ChunkOrderApplied { get; set; } = "default";

    /// <summary>
    /// 本次實際套用的排列表：第 i 項代表「新順序的第 i 個位置放的是檢索結果的第幾段」。
    /// 例如 5 段做 reverse 會是 [4,3,2,1,0]。
    ///
    /// 每次請求都自帶排列，是為了讓結果事後可還原——尤其 shuffle，
    /// 沒有這個欄位就無法把數字和當時的排列對應起來。
    /// </summary>
    public List<int> ChunkPermutation { get; set; } = new();

    /// <summary>shuffle 實際使用的種子；其餘模式為 null。</summary>
    public int? ChunkOrderSeedUsed { get; set; }

    /// <summary>
    /// 純前綴快取在本次請求最多能命中的 token 數（見 ChunkOrderStrategy.PrefixOnlyCeiling）。
    ///
    /// 判讀方式：
    ///   CachedTokens 明顯大於此值 → 高命中只能由非前綴複用解釋，即 CacheBlend 生效
    ///   CachedTokens 約等於此值   → 無法排除只是一般前綴快取在作用
    ///
    /// 這個欄位存在的理由：「命中率 99.98%」單獨看不構成 CacheBlend 的證據，
    /// 必須與前綴快取能達到的上限對照才有意義。
    /// </summary>
    public int PrefixOnlyCeilingTokens { get; set; }

    /// <summary>CachedTokens 與前綴上限的倍數。大於 1 表示超出前綴快取所能解釋的範圍。</summary>
    public double CachedOverPrefixCeiling { get; set; }
}
