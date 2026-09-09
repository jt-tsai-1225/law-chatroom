namespace ChatBot.Api.Models;

/// <summary>
/// 基準測試請求
/// </summary>
public class BenchmarkRequest
{
    /// <summary>
    /// 測試問題列表
    /// </summary>
    public required List<string> Questions { get; set; }
    
    /// <summary>
    /// 每個問題執行的次數
    /// </summary>
    public int Iterations { get; set; } = 3;
}

/// <summary>
/// 基準測試結果
/// </summary>
public class BenchmarkResult
{
    /// <summary>
    /// 問題內容
    /// </summary>
    public string Question { get; set; } = "";
    
    /// <summary>
    /// 平均 TTFT (毫秒)
    /// </summary>
    public double AverageTtftMs { get; set; }
    
    /// <summary>
    /// 最小 TTFT (毫秒)
    /// </summary>
    public double MinTtftMs { get; set; }
    
    /// <summary>
    /// 最大 TTFT (毫秒)
    /// </summary>
    public double MaxTtftMs { get; set; }
    
    /// <summary>
    /// 標準差 (毫秒)
    /// </summary>
    public double StdDevTtftMs { get; set; }
    
    /// <summary>
    /// 平均總耗時 (毫秒)
    /// </summary>
    public double AverageTotalMs { get; set; }
    
    /// <summary>
    /// 平均 Token 數
    /// </summary>
    public double AverageTokens { get; set; }
    
    /// <summary>
    /// 平均吞吐量 (tokens/秒)
    /// </summary>
    public double AverageThroughput { get; set; }
    
    /// <summary>
    /// 每次執行的詳細結果
    /// </summary>
    public List<IterationResult> Iterations { get; set; } = new();
}

/// <summary>
/// 單次執行結果
/// </summary>
public class IterationResult
{
    public int Iteration { get; set; }
    public long TtftMs { get; set; }
    public long TotalMs { get; set; }
    public int TokenCount { get; set; }
    public double Throughput { get; set; }
    public bool FromCache { get; set; }
}

/// <summary>
/// 基準測試回應
/// </summary>
public class BenchmarkResponse
{
    public string Setting { get; set; } = "";
    public List<BenchmarkResult> Results { get; set; } = new();
    public SummaryStats Summary { get; set; } = new();
}

/// <summary>
/// 統計摘要
/// </summary>
public class SummaryStats
{
    public double AverageTtftMs { get; set; }
    public double AverageTotalMs { get; set; }
    public double AverageThroughput { get; set; }
    public double CacheHitRate { get; set; }
}
