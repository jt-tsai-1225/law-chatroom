using ChatBot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

public sealed class StoredObject
{
    /// <summary>取回這份檔案用的識別碼。本地實作為相對路徑，SeaweedFS 實作為 fid。</summary>
    public string Key { get; init; } = "";

    public string FileName { get; init; } = "";
    public long SizeBytes { get; init; }
    public string ContentType { get; init; } = "";
    public DateTime StoredAt { get; init; } = DateTime.UtcNow;

    /// <summary>實際落地的位置，僅供記錄與除錯，不保證是可公開的網址。</summary>
    public string Location { get; init; } = "";
}

/// <summary>
/// 原始檔案的儲存。
///
/// 為什麼需要這層抽象：規劃中原始 PDF 要放 SeaweedFS，但 SeaweedFS 尚未架設。
/// 若讓上傳流程直接寫本機磁碟，等 SeaweedFS 上線時得回頭改動流程本身；
/// 先立介面，之後只換實作、流程不動。
///
/// 為什麼一定要留原始檔：Qdrant 存的是切分後的片段，切分規則會改
/// （2026/09/24 就整組改過一次）。沒有原始檔就無法重新切分，
/// 等於每次調整顆粒度都要重新去找當初的 PDF。
/// </summary>
public interface IObjectStorage
{
    Task<StoredObject> SaveAsync(
        Stream content, string fileName, string contentType, CancellationToken ct = default);

    Task<Stream?> OpenAsync(string key, CancellationToken ct = default);

    Task<bool> DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>目前生效的後端名稱，供診斷與回應顯示。</summary>
    string BackendName { get; }
}

/// <summary>
/// 本機磁碟實作。SeaweedFS 架好之前的過渡方案。
///
/// 存放位置由 RAGSettings.ObjectStorageRoot 決定，預設在容器內的 uploads/。
/// ⚠ 容器內的路徑不具持久性——要保留原始檔必須把該目錄掛成 volume，
/// 否則重建容器就消失。這是過渡方案的已知限制，SeaweedFS 上線後解除。
/// </summary>
public class LocalObjectStorage : IObjectStorage
{
    private readonly string _root;
    private readonly ILogger<LocalObjectStorage> _logger;

    public string BackendName => "local-disk";

    public LocalObjectStorage(
        IOptions<RAGSettings> settings,
        ILogger<LocalObjectStorage> logger,
        IWebHostEnvironment env)
    {
        var configured = settings.Value.ObjectStorageRoot;

        _root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(env.ContentRootPath, configured);

        _logger = logger;
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredObject> SaveAsync(
        Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        // 以日期分層，避免單一目錄累積過多檔案
        var folder = Path.Combine(_root, DateTime.UtcNow.ToString("yyyyMM"));
        Directory.CreateDirectory(folder);

        // 保留原始檔名以便人工辨識，前面加上一段亂碼避免同名覆蓋。
        // Path.GetFileName 是必要的：上傳來的檔名可能含有 ../ 之類的路徑片段。
        var safeName = Path.GetFileName(fileName);
        var stored = $"{Guid.NewGuid():N}_{safeName}";
        var fullPath = Path.Combine(folder, stored);

        await using (var fs = File.Create(fullPath))
        {
            await content.CopyToAsync(fs, ct);
        }

        var info = new FileInfo(fullPath);
        var key = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');

        _logger.LogInformation(
            "已保存原始檔 {FileName}（{Size:N0} bytes）→ {Key}", safeName, info.Length, key);

        return new StoredObject
        {
            Key = key,
            FileName = safeName,
            SizeBytes = info.Length,
            ContentType = contentType,
            Location = fullPath
        };
    }

    public Task<Stream?> OpenAsync(string key, CancellationToken ct = default)
    {
        var path = ResolveWithinRoot(key);

        if (path is null || !File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        return Task.FromResult<Stream?>(File.OpenRead(path));
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = ResolveWithinRoot(key);

        if (path is null || !File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 把 key 解析成絕對路徑，並確認它確實落在儲存根目錄之內。
    /// key 來自外部輸入，未經檢查時 "../../etc/passwd" 這類值會讀到根目錄以外。
    /// </summary>
    private string? ResolveWithinRoot(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var candidate = Path.GetFullPath(Path.Combine(_root, key));
        var root = Path.GetFullPath(_root);

        var normalizedRoot = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(normalizedRoot, StringComparison.Ordinal) ? candidate : null;
    }
}
