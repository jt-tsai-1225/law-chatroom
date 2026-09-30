using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

/// <summary>解析出來的一個條文片段。欄位名稱與 tools/parse_pdf.py 的輸出一致。</summary>
public sealed class ParsedChunk
{
    /// <summary>由內容決定的 UUID，重新上傳同一份檔案會得到相同的值。</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("title")] public string Title { get; set; } = "";

    /// <summary>
    /// 片段全文，**已包含條號那一行**。
    /// 這就是快取鍵的來源，前面不可再補任何隨位置變動的標記。
    /// </summary>
    [JsonPropertyName("content")] public string Content { get; set; } = "";

    [JsonPropertyName("chapter")] public string Chapter { get; set; } = "";
    [JsonPropertyName("section")] public string Section { get; set; } = "";

    /// <summary>顯示用標籤，例如「第 291、292、293 條」。</summary>
    [JsonPropertyName("article")] public string Article { get; set; } = "";

    [JsonPropertyName("articles")] public List<string> Articles { get; set; } = new();
    [JsonPropertyName("nChars")] public int NChars { get; set; }
    [JsonPropertyName("sourceFile")] public string SourceFile { get; set; } = "";
}

public sealed class ParseResult
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("sourceFile")] public string SourceFile { get; set; } = "";
    [JsonPropertyName("articleCount")] public int ArticleCount { get; set; }
    [JsonPropertyName("chunks")] public List<ParsedChunk> Chunks { get; set; } = new();

    /// <summary>解析不出條文時的說明，成功時為 null。</summary>
    [JsonPropertyName("warning")] public string? Warning { get; set; }
}

public interface ILegalDocumentParser
{
    Task<ParseResult> ParsePdfAsync(string pdfPath, string? title, CancellationToken ct = default);
    Task<ParseResult> ParseTextAsync(string text, string title, string source, CancellationToken ct = default);
}

/// <summary>
/// 以子行程呼叫 tools/parse_pdf.py 做解析。
///
/// ── 為什麼不在 C# 裡解析 ────────────────────────────────────────
/// 條文切分曾經整組失效過：1,972 條條文被切成 270 個標籤錯亂的片段，
/// 而且是靜默發生的——TTFT 與命中率都正常，只有答案引用了不存在的條號。
/// 修好的那份實作在 Python，經過實測（327 片段、0 重複標籤）。
///
/// 在 C# 重新實作意味著換一個 PDF 文字引擎，行文斷行的結果未必相同，
/// 而條號比對是**整行比對**——斷行一變，整個切分就再次失效，同樣靜默。
/// 與其賭行為相同，不如直接跑那份已驗證的程式。
///
/// 代價是後端映像要裝 python3 與 pymupdf，寫在 Dockerfile 裡。
/// </summary>
public class LegalDocumentParser : ILegalDocumentParser
{
    private readonly RAGSettings _settings;
    private readonly ILogger<LegalDocumentParser> _logger;
    private readonly string _scriptPath;

    public LegalDocumentParser(
        IOptions<RAGSettings> settings,
        ILogger<LegalDocumentParser> logger,
        IWebHostEnvironment env)
    {
        _settings = settings.Value;
        _logger = logger;

        _scriptPath = Path.IsPathRooted(_settings.ParserScriptPath)
            ? _settings.ParserScriptPath
            : Path.Combine(env.ContentRootPath, _settings.ParserScriptPath);
    }

    public Task<ParseResult> ParsePdfAsync(string pdfPath, string? title, CancellationToken ct = default)
    {
        var args = new List<string> { _scriptPath, "--file", pdfPath };
        if (!string.IsNullOrWhiteSpace(title))
        {
            args.Add("--title");
            args.Add(title!);
        }

        return RunAsync(args, stdin: null, ct);
    }

    public Task<ParseResult> ParseTextAsync(
        string text, string title, string source, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            _scriptPath, "--text", "--title", title, "--source", source
        };

        return RunAsync(args, stdin: text, ct);
    }

    private async Task<ParseResult> RunAsync(
        IReadOnlyList<string> args, string? stdin, CancellationToken ct)
    {
        if (!File.Exists(_scriptPath))
        {
            throw new FileNotFoundException(
                $"找不到解析腳本 {_scriptPath}。後端映像必須包含 tools/parse_pdf.py，" +
                "並安裝 python3 與 pymupdf（見 Dockerfile）", _scriptPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _settings.PythonExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // 解析腳本把中文寫到 stdout，直譯器的 I/O 編碼必須明確指定，
        // 否則在沒有 locale 的精簡容器裡會退成 ASCII 而拋錯
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = psi };

        var sw = Stopwatch.StartNew();
        process.Start();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            process.StandardInput.Close();
        }

        // stdout 與 stderr 必須同時讀。只讀其一而另一邊寫滿管線緩衝區時，
        // 子行程會卡在寫入、父行程會卡在等它結束——典型的互相等待。
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            // 腳本把進度訊息寫到 stderr，那是正常的，不是錯誤
            _logger.LogInformation("解析腳本訊息：{Message}", stderr.Trim());
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"解析腳本以代碼 {process.ExitCode} 結束：{stderr.Trim()}");
        }

        var result = Deserialize(stdout);

        if (result is null)
        {
            throw new InvalidOperationException("解析腳本沒有輸出任何結果");
        }

        _logger.LogInformation(
            "解析完成：{Title} — {Articles} 條 → {Chunks} 個片段，耗時 {Ms} ms",
            result.Title, result.ArticleCount, result.Chunks.Count, sw.ElapsedMilliseconds);

        return result;
    }

    /// <summary>
    /// 把腳本的 stdout 轉成結果。
    ///
    /// 腳本保證 stdout 只有 JSON，但那個保證依賴第三方套件守規矩，而它們不一定守：
    /// pymupdf 以舊名匯入時會往 stdout 印一行棄用警告，整份 JSON 就此失效。
    /// 腳本端已經把工作期間的 stdout 導開，這裡再補一層——
    /// 解析明明成功了（五百多條條文都在），只因為前面多一行字就整批丟掉，
    /// 代價與風險完全不成比例。
    ///
    /// 退而求其次時會記一筆警告，不讓它靜悄悄地被容忍掉。
    /// </summary>
    private ParseResult Deserialize(string stdout)
    {
        try
        {
            var direct = JsonSerializer.Deserialize<ParseResult>(stdout);
            if (direct is not null) return direct;
        }
        catch (JsonException)
        {
            // 落到下面的容錯路徑
        }

        var start = stdout.IndexOf('{');
        var end = stdout.LastIndexOf('}');

        if (start >= 0 && end > start)
        {
            var slice = stdout[start..(end + 1)];
            try
            {
                var salvaged = JsonSerializer.Deserialize<ParseResult>(slice);
                if (salvaged is not null)
                {
                    _logger.LogWarning(
                        "解析腳本的 stdout 混入了非 JSON 內容，已略過前後雜訊後取用。" +
                        "前綴內容：{Prefix}", Truncate(stdout[..start], 200));
                    return salvaged;
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"解析腳本的輸出不是有效的 JSON：{Truncate(stdout, 500)}", ex);
            }
        }

        throw new InvalidOperationException(
            $"解析腳本的輸出不是有效的 JSON：{Truncate(stdout, 500)}");
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "(空)" : s.Length <= max ? s : s[..max] + "…";
}
