namespace ChatBot.Api.Services;

/// <summary>
/// 三種快取組態。每一種都是一個獨立的 vLLM 實例，因為 LMCache 的設定
/// 在引擎啟動時就固定了，無法用單次請求的參數切換。
///
/// ⚠ 一張 48 GB 的卡放不下三個實例：Mistral-7B fp16 的權重每份約 14 GB，
///   三份就 43.5 GB，連 KV 池都不剩。因此預設只常駐兩個
///  （CacheBlend 與純 LMCache，各 --gpu-memory-utilization 0.45），
///   「無快取」由管理者按需切換（見 deploy/README.md）。
///
///   沒設定端點的模式不會靜默退回預設——那會讓使用者以為自己在比較
///   兩種快取，實際上打的是同一個引擎。缺端點時一律明確回報。
/// </summary>
public static class CacheModes
{
    /// <summary>CacheBlend：以片段內容雜湊為鍵，與位置無關。</summary>
    public const string CacheBlend = "cacheblend";

    /// <summary>純 LMCache：一般的 256-token 區塊前綴比對。</summary>
    public const string LmCache = "lmcache";

    /// <summary>不掛任何 KV 快取，每次完整重算。</summary>
    public const string None = "none";

    public const string Default = CacheBlend;

    public static readonly IReadOnlyList<string> All = new[] { CacheBlend, LmCache, None };

    /// <summary>
    /// 正規化使用者傳來的值。無法辨識時回預設值——這裡退回預設是安全的，
    /// 因為「無法辨識」通常是前端傳錯，而非使用者刻意選擇某個模式。
    /// 刻意選了卻沒有端點的情況，由 LlmEndpointRegistry 擋下並回報。
    /// </summary>
    public static string Normalize(string? raw)
    {
        var value = (raw ?? "").Trim().ToLowerInvariant();

        return value switch
        {
            "" or "default" or CacheBlend or "blend" => CacheBlend,
            LmCache or "lm" or "prefix" => LmCache,
            None or "nocache" or "no-cache" or "off" => None,
            _ => Default
        };
    }

    /// <summary>給介面顯示用的名稱。</summary>
    public static string DisplayName(string mode) => mode switch
    {
        CacheBlend => "CacheBlend（非前綴複用）",
        LmCache => "純 LMCache（前綴快取）",
        None => "無快取",
        _ => mode
    };
}
