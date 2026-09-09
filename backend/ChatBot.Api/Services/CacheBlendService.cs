using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ChatBot.Api.Configuration;

namespace ChatBot.Api.Services;

/// <summary>
/// CacheBlend 服務實作 - 智能混合快取內容和新計算內容
/// 
/// 核心功能：
/// 1. 相似度計算：使用 TF-IDF 和關鍵字重疊來計算快取項目與當前請求的相似度
/// 2. 內容混合：根據相似度分數，混合快取內容和新計算的內容
/// 3. 部分重用：即使沒有完全匹配的快取，也能重用部分計算結果
/// </summary>
public class CacheBlendService : ICacheBlendService
{
    private readonly ILogger<CacheBlendService> _logger;
    private readonly bool _enabled;
    private readonly string _blendStrategy;
    private readonly double _similarityThreshold;
    private readonly int _topK;
    private readonly double _blendWeight;
    private readonly double _minBlendWeight;
    private readonly double _tokenWeight;
    private readonly double _lengthWeight;
    
    // 統計資訊
    private long _blendCount = 0;
    private long _exactHitCount = 0;
    private long _fullComputeCount = 0;

    public CacheBlendService(
        IOptions<RAGSettings>? ragSettings = null,
        ILogger<CacheBlendService>? logger = null)
    {
        _logger = logger ?? LoggerFactory.Create(builder => { }).CreateLogger<CacheBlendService>();
        var settings = ragSettings?.Value ?? new RAGSettings();
        _enabled = settings.CacheBlendEnabled;
        _blendStrategy = settings.CacheBlendStrategy;
        _similarityThreshold = settings.CacheBlendSimilarityThreshold;
        _topK = settings.CacheBlendTopK;
        _blendWeight = settings.CacheBlendWeight;
        _minBlendWeight = 0.5;
        _tokenWeight = 0.7;
        _lengthWeight = 0.3;
        
        _logger.LogInformation("CacheBlend 初始化完成 - 啟用狀態: {Enabled}, 混合策略: {Strategy}",
            _enabled, _blendStrategy);
    }

    public bool IsEnabled => _enabled;
    
    // Helper properties to replace BlendSettings
    private string BlendStrategy => _blendStrategy;
    private double SimilarityThreshold => _similarityThreshold;
    private int TopK => _topK;
    private double BlendWeight => _blendWeight;
    private double MinBlendWeight => _minBlendWeight;
    private double TokenWeight => _tokenWeight;
    private double LengthWeight => _lengthWeight;

    /// <summary>
    /// 根據相似度和混合策略，混合快取內容和新的 LLM 回應
    /// </summary>
    public string Blend(
        IEnumerable<CacheEntry> cacheEntries, 
        string newResponse, 
        BlendContext requestContext)
    {
        if (!IsEnabled)
        {
            Interlocked.Increment(ref _fullComputeCount);
            return newResponse;
        }

        if (cacheEntries == null || !cacheEntries.Any())
        {
            Interlocked.Increment(ref _fullComputeCount);
            return newResponse;
        }

        // 計算每個快取項目的相似度分數
        var prompt = $"{requestContext.SystemPrompt}\n{requestContext.UserPrompt}";
        var scoredEntries = cacheEntries
            .Select(entry => (Entry: entry, Similarity: CalculateSimilarity(prompt, entry)))
            .Where(x => x.Similarity >= SimilarityThreshold)
            .OrderByDescending(x => x.Similarity)
            .Take(TopK)
            .ToList();

        if (!scoredEntries.Any())
        {
            Interlocked.Increment(ref _fullComputeCount);
            _logger.LogDebug("沒有找到足夠相似的快取項目，使用完整計算");
            return newResponse;
        }

        // 如果最高相似度接近 1.0，視為精確命中
        var topSimilarity = scoredEntries.First().Similarity;
        if (topSimilarity >= 0.95)
        {
            Interlocked.Increment(ref _exactHitCount);
            _logger.LogDebug("精確命中 (similarity={Similarity})，直接返回快取內容", topSimilarity);
            return scoredEntries.First().Entry.Value;
        }

        // 執行混合策略
        Interlocked.Increment(ref _blendCount);
        
        var blendedResult = ApplyBlendStrategy(scoredEntries, newResponse, topSimilarity);
        
        _logger.LogDebug("CacheBlend 混合完成 - 最高相似度: {Similarity}, 混合效率: {Efficiency}%",
            topSimilarity, topSimilarity * 100);

        return blendedResult;
    }

    /// <summary>
    /// 計算快取項目與當前請求的相似度分數
    /// 使用關鍵字重疊和詞幹匹配
    /// </summary>
    public double CalculateSimilarity(string currentPrompt, CacheEntry cacheEntry)
    {
        if (string.IsNullOrEmpty(currentPrompt) || string.IsNullOrEmpty(cacheEntry.Key))
            return 0.0;

        // 1. 關鍵字重疊計數 (Jaccard Similarity)
        // 注意：cacheEntry.Key 是 SHA256 雜湊字串，不能拿來做文字相似度比對，
        // 必須使用儲存時一併保留下來的原始 prompt (SourcePrompt)。
        if (string.IsNullOrEmpty(cacheEntry.SourcePrompt))
            return 0.0;

        var currentTokens = Tokenize(currentPrompt);
        var cachedTokens = Tokenize(cacheEntry.SourcePrompt);
        
        var currentSet = new HashSet<string>(currentTokens);
        var cachedSet = new HashSet<string>(cachedTokens);
        
        var intersection = currentSet.Intersect(cachedSet).Count();
        var union = currentSet.Union(cachedSet).Count();
        
        var jaccardSimilarity = union > 0 ? (double)intersection / union : 0.0;

        // 2. 長度相似度（長短差距越小，相似度越高）
        var currentLength = currentPrompt.Length;
        var cachedLength = cacheEntry.Value.Length;
        var maxLength = Math.Max(currentLength, cachedLength);
        var minLength = Math.Min(currentLength, cachedLength);
        var lengthSimilarity = maxLength > 0 ? (double)minLength / maxLength : 0.0;

        // 3. 權重組合
        var finalSimilarity = (jaccardSimilarity * TokenWeight) +
                              (lengthSimilarity * LengthWeight);

        return Math.Min(finalSimilarity, 1.0);
    }

    /// <summary>
    /// 獲取 CacheBlend 統計資訊
    /// </summary>
    public BlendStats GetStats()
    {
        return new BlendStats
        {
            BlendCount = Interlocked.Read(ref _blendCount),
            ExactHitCount = Interlocked.Read(ref _exactHitCount),
            FullComputeCount = Interlocked.Read(ref _fullComputeCount)
        };
    }

    /// <summary>
    /// 應用混合策略
    /// </summary>
    private string ApplyBlendStrategy(
        List<(CacheEntry Entry, double Similarity)> scoredEntries, 
        string newResponse, 
        double topSimilarity)
    {
        switch (BlendStrategy)
        {
            case "similarity":
                return ApplySimilarityBlend(scoredEntries, newResponse, topSimilarity);
            
            case "priority":
                return ApplyPriorityBlend(scoredEntries, newResponse);
            
            case "sequential":
                return ApplySequentialBlend(scoredEntries, newResponse);
            
            default:
                return ApplySimilarityBlend(scoredEntries, newResponse, topSimilarity);
        }
    }

    /// <summary>
    /// 基於相似度的混合策略
    /// 根據相似度分數調整快取內容和新內容的比例
    /// </summary>
    private string ApplySimilarityBlend(
        List<(CacheEntry Entry, double Similarity)> scoredEntries, 
        string newResponse, 
        double topSimilarity)
    {
        // 主要使用相似度最高的快取項目
        var topEntry = scoredEntries.First();
        var blendWeight = topEntry.Similarity * BlendWeight;

        // 混合快取內容和新內容
        var cachedContent = topEntry.Entry.Value;
        
        // 根據權重進行混合
        if (blendWeight >= MinBlendWeight)
        {
            // 高相似度：主要使用快取內容，補充新內容
            var cachedPrefixLength = (int)(cachedContent.Length * blendWeight);
            var newSuffixLength = (int)(newResponse.Length * (1 - blendWeight));
            
            var cachedPart = cachedContent.Substring(0, Math.Min(cachedPrefixLength, cachedContent.Length));
            var newPart = newSuffixLength > 0 && newResponse.Length > cachedPrefixLength
                ? newResponse.Substring(Math.Min(cachedPrefixLength, newResponse.Length), 
                    Math.Min(newSuffixLength, newResponse.Length - Math.Min(cachedPrefixLength, newResponse.Length)))
                : "";
            
            _logger.LogDebug("CacheBlend 相似度混合 - 快取權重: {Weight}, 快取長度: {CachedLen}, 新內容長度: {NewLen}",
                blendWeight, cachedContent.Length, newResponse.Length);

            return $"{cachedPart}{(string.IsNullOrEmpty(newPart) ? "" : "\n\n--- 補充說明：\n" + newPart)}";
        }

        return newResponse;
    }

    /// <summary>
    /// 基於優先級的混合策略
    /// 根據快取項目的優先級（訪問次數、創建時間等）進行混合
    /// </summary>
    private string ApplyPriorityBlend(
        List<(CacheEntry Entry, double Similarity)> scoredEntries, 
        string newResponse)
    {
        // 根據訪問次數和相似度計算優先級
        var prioritizedEntries = scoredEntries
            .Select(e => new
            {
                e.Entry,
                e.Similarity,
                Priority = e.Similarity * Math.Min(e.Entry.AccessCount / 10.0, 1.0)
            })
            .OrderByDescending(x => x.Priority)
            .ToList();

        if (prioritizedEntries.Any() && prioritizedEntries.First().Priority > MinBlendWeight)
        {
            return prioritizedEntries.First().Entry.Value;
        }

        return newResponse;
    }

    /// <summary>
    /// 基於順序的混合策略
    /// 將快取內容和新內容按順序組合成完整的回應
    /// </summary>
    private string ApplySequentialBlend(
        List<(CacheEntry Entry, double Similarity)> scoredEntries, 
        string newResponse)
    {
        var builder = new StringBuilder();
        
        foreach (var entry in scoredEntries)
        {
            builder.AppendLine($"[快取參考] {entry.Entry.Value}");
            builder.AppendLine("---");
        }
        
        builder.AppendLine(newResponse);
        
        return builder.ToString();
    }

    /// <summary>
    /// 將文本 tokenize 為詞元列表（基於中文分詞和英文單詞）
    /// </summary>
    private List<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        
        var tokens = new List<string>();
        var lowerText = text.ToLowerInvariant();
        
        // 移除常見停詞
        var stopWords = new HashSet<string> 
        { 
            "的", "了", "是", "在", "和", "就", "而", "且", "與", "或", "若", "此",
            "a", "an", "the", "and", "or", "but", "in", "on", "at", "to", "for",
            "of", "with", "by", "is", "are", "was", "were"
        };

        // 簡易分詞：英文按單詞，中文按字
        var currentToken = new StringBuilder();
        foreach (var c in lowerText)
        {
            if (char.IsWhiteSpace(c) || c == ',' || c == '.' || c == ':' || c == ';')
            {
                if (currentToken.Length > 0)
                {
                    var token = currentToken.ToString();
                    if (!stopWords.Contains(token))
                    {
                        tokens.Add(token);
                    }
                    currentToken.Clear();
                }
            }
            else
            {
                currentToken.Append(c);
            }
        }
        
        if (currentToken.Length > 0)
        {
            var lastToken = currentToken.ToString();
            if (!stopWords.Contains(lastToken))
            {
                tokens.Add(lastToken);
            }
        }

        return tokens;
    }
}

/// <summary>
/// CacheBlend 設定選項
/// </summary>
public class BlendSettings
{
    public const string SectionName = "CacheBlend";

    /// <summary>
    /// 是否啟用 CacheBlend
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 混合策略 (similarity/priority/sequential)
    /// </summary>
    public string BlendStrategy { get; set; } = "similarity";

    /// <summary>
    /// 相似度閾值，低於此值的快取項目不會被考慮
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.3;

    /// <summary>
    /// 考慮前 K 個最相似的快取項目
    /// </summary>
    public int TopK { get; set; } = 3;

    /// <summary>
    /// 混合權重（0-1），越高表示越依賴快取內容
    /// </summary>
    public double BlendWeight { get; set; } = 0.7;

    /// <summary>
    /// 最小混合權重，低於此值則不進行混合
    /// </summary>
    public double MinBlendWeight { get; set; } = 0.5;

    /// <summary>
    /// 關鍵字重疊權重
    /// </summary>
    public double TokenWeight { get; set; } = 0.7;

    /// <summary>
    /// 長度相似度權重
    /// </summary>
    public double LengthWeight { get; set; } = 0.3;
}
