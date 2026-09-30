namespace ChatBot.Api.Services;

/// <summary>
/// 決定條文片段在 prompt 中的排列方式。
///
/// 為什麼要把排列抽成獨立的策略：
///   要驗證「同一組片段換個位置，KV 是否仍被複用」，必須讓**片段組合完全不變、
///   只有順序改變**。正常問答做不到——換一個問題就檢索到另一批片段，
///   問同一個問題順序又永遠一樣。因此把排列拉成單次請求的參數。
///
/// 各模式的設計意圖不同，不是「多幾種隨機排列」而已：
///
///   relevance   基準線。與快取中的順序相同，前綴完全一致。
///   swap-tail   只動最後兩段。前面數段位置不變，**一般前綴快取本來就能吃到一大半**，
///               因此這一組是用來測「前綴快取單獨能做到多少」的對照組。
///   swap-head   只動前兩段。第一段一換，前綴從第一個片段就分岔，
///               前綴快取的理論上限瞬間掉到只剩固定前綴。
///   reverse     整個倒轉。與 swap-head 同樣只剩固定前綴，但每一段都換了位置。
///   shuffle     完全打亂。以 seed 決定，可重現。
///
/// swap-tail 與 swap-head 的對比是本組實驗的核心：
///   若兩者的命中率都接近 100%，而前綴快取的理論上限一個是六成、一個是 3%，
///   那麼高命中率就不可能由前綴快取解釋。
/// </summary>
public static class ChunkOrderStrategy
{
    public const string Default = "default";
    public const string Relevance = "relevance";
    public const string Reverse = "reverse";
    public const string Shuffle = "shuffle";
    public const string SwapHead = "swap-head";
    public const string SwapTail = "swap-tail";

    /// <summary>
    /// shuffle 的預設亂數種子。固定值使結果可重現——驗證報告中的數字
    /// 必須能被重跑出來，隨機且不記錄的排列無法作為證據。
    /// </summary>
    public const int DefaultSeed = 20260930;

    /// <summary>
    /// 把請求帶進來的字串正規化成正式的模式名稱。
    /// "asis" 是舊名，保留為別名以免先前的測試紀錄對不上；正式名稱為 "relevance"。
    /// 無法辨識的值一律退回 Default，而非拋出例外——排列方式是驗證用的旋鈕，
    /// 打錯字不應該讓使用者的問答失敗。
    /// </summary>
    public static string Normalize(string? raw)
    {
        var mode = (raw ?? Default).Trim().ToLowerInvariant();

        return mode switch
        {
            "asis" or "as-is" or Relevance => Relevance,
            Reverse => Reverse,
            Shuffle => Shuffle,
            SwapHead or "swaphead" or "swap_head" => SwapHead,
            SwapTail or "swaptail" or "swap_tail" => SwapTail,
            _ => Default
        };
    }

    /// <summary>此模式是否為驗證用的明確排列（亦即不走依快取狀態重排）。</summary>
    public static bool IsExplicit(string mode) => mode != Default;

    /// <summary>
    /// 產生排列表。回傳值的第 i 項，是「新順序中第 i 個位置要放原本的第幾段」。
    /// 例如 5 段做 reverse 會得到 [4,3,2,1,0]。
    ///
    /// 回傳排列表而非直接回傳重排後的清單，是為了讓它能被原樣寫進回應——
    /// 每一次請求都自帶它實際用了什麼排列，事後不必靠猜或靠當時的筆記還原。
    /// </summary>
    public static List<int> BuildPermutation(string mode, int count, int seed)
    {
        var identity = Enumerable.Range(0, count).ToList();

        if (count < 2)
        {
            return identity;
        }

        switch (mode)
        {
            case Reverse:
                return Enumerable.Range(0, count).Reverse().ToList();

            case SwapHead:
            {
                var p = new List<int>(identity);
                (p[0], p[1]) = (p[1], p[0]);
                return p;
            }

            case SwapTail:
            {
                var p = new List<int>(identity);
                (p[count - 2], p[count - 1]) = (p[count - 1], p[count - 2]);
                return p;
            }

            case Shuffle:
                return Shuffled(count, seed);

            // Relevance 與 Default 都不改順序；Default 的重排另由呼叫端處理
            default:
                return identity;
        }
    }

    /// <summary>
    /// 以固定演算法做 Fisher-Yates 洗牌。
    ///
    /// 這裡刻意不用 System.Random：它的內部演算法在不同 .NET 版本之間變動過，
    /// 同一個 seed 不保證跨版本得到相同結果。驗證報告的數字必須能在數個月後、
    /// 換一個執行環境重跑出來，因此自帶一個規格固定的 xorshift32。
    ///
    /// 另外會避開「洗出來剛好等於原順序」的情況——那會產生一次名為 shuffle
    /// 實則沒有換位的執行，是最容易誤導判讀的結果。
    /// </summary>
    private static List<int> Shuffled(int count, int seed)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var state = unchecked((uint)(seed + attempt));
            if (state == 0) state = 0x9E3779B9;   // xorshift 不允許全 0 狀態

            var p = Enumerable.Range(0, count).ToList();

            for (var i = count - 1; i > 0; i--)
            {
                var j = (int)(NextRandom(ref state) % (uint)(i + 1));
                (p[i], p[j]) = (p[j], p[i]);
            }

            if (!IsIdentity(p))
            {
                return p;
            }
        }

        // 理論上到不了這裡（count >= 2 時連續 8 次洗出原順序的機率可忽略），
        // 但與其回傳原順序讓人誤以為洗過了，不如退成 reverse——至少確實換了位置。
        return Enumerable.Range(0, count).Reverse().ToList();
    }

    private static uint NextRandom(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static bool IsIdentity(IReadOnlyList<int> p)
    {
        for (var i = 0; i < p.Count; i++)
        {
            if (p[i] != i) return false;
        }
        return true;
    }

    /// <summary>
    /// 算出「純前綴快取在這次請求最多能命中幾個 token」。
    ///
    /// 判讀命中率時這是不可或缺的對照。CacheBlend 的價值主張是
    /// 「片段換了位置仍然複用」，而前綴快取只要 prompt 從某個 token 起分岔，
    /// 之後就全部失效。因此：
    ///
    ///   實測 cachedTokens 明顯大於這個上限 → 只能由非前綴複用解釋
    ///   實測 cachedTokens 落在這個上限附近 → 無法排除只是前綴快取在作用
    ///
    /// 算法：排列表從第 0 項開始逐項比對，找出第一個與原順序不同的位置 k，
    /// prompt 在該片段的第一個 token 處才開始分岔，因此上限為 chunkOffsets[k]
    /// （分隔符在所有排列中都相同，故包含在內）。
    ///
    /// ⚠ 這個數字建立在一個假設上：快取中存在的是**相關度原序**那一版 prompt。
    ///   若快取中還有其他排列（例如先前跑過 reverse），真正的前綴上限會更高。
    ///   因此測試應從乾淨的快取開始，並以 relevance 那次先建立基準。
    /// </summary>
    public static int PrefixOnlyCeiling(
        IReadOnlyList<int> permutation,
        IReadOnlyList<int> chunkOffsets,
        int totalPromptTokens)
    {
        for (var k = 0; k < permutation.Count; k++)
        {
            if (permutation[k] != k)
            {
                return k < chunkOffsets.Count ? chunkOffsets[k] : totalPromptTokens;
            }
        }

        // 排列與原順序完全相同，整段 prompt 都可由前綴快取供應
        return totalPromptTokens;
    }
}
