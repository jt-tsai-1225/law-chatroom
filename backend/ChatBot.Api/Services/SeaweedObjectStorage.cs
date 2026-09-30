using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ChatBot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

/// <summary>
/// 以 SeaweedFS 的 S3 相容閘道保存原始檔。
///
/// ── 為什麼走 S3 API 而不是 SeaweedFS 原生 API ──────────────────
/// 原生 API 是兩段式的：先跟 master 要一個 fid（`3,01637037d6` 這種），
/// 再把位元組 PUT 到指定的 volume server，而 fid 得由呼叫端自己保管。
/// S3 閘道把這些包成一般的物件儲存語意，可以直接用成熟的 AWS SDK，
/// 不必自己處理 fid 的配發、保存與失效。
///
/// 代價是多一層閘道。對「偶爾存取一次原始檔」這種用途，這個代價可忽略；
/// 若哪天要走高吞吐的批次匯入，再評估原生 API。
///
/// ── 為什麼原始檔要留 ────────────────────────────────────────────
/// Qdrant 存的是切分後的片段，而切分規則會改（2026/09/24 就整組改過一次，
/// 見報告 14.2）。沒有原始檔就無法重新切分，每次調整顆粒度都得回頭
/// 找當初的 PDF——2026/09/30 實際發生過一次。
/// </summary>
public class SeaweedObjectStorage : IObjectStorage
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly ILogger<SeaweedObjectStorage> _logger;

    /// <summary>確保 bucket 存在只需做一次，用它擋掉重複檢查。</summary>
    private readonly SemaphoreSlim _bucketGate = new(1, 1);
    private bool _bucketReady;

    public string BackendName => "seaweedfs-s3";

    public SeaweedObjectStorage(
        IOptions<RAGSettings> settings,
        ILogger<SeaweedObjectStorage> logger)
    {
        var s = settings.Value;
        _bucket = s.S3Bucket;
        _logger = logger;

        var config = new AmazonS3Config
        {
            ServiceURL = s.S3ServiceUrl,

            // 非 AWS 的 S3 實作幾乎都要這個。預設的 virtual-host 定址會把
            // bucket 名稱放進主機名（law-documents.seaweedfs:8333），
            // 那個主機名不存在，請求會直接解析失敗。
            ForcePathStyle = true,

            // SeaweedFS 不分區域，但 SDK 要求有值才簽得出簽章
            AuthenticationRegion = s.S3Region
        };

        _s3 = new AmazonS3Client(
            new BasicAWSCredentials(s.S3AccessKey, s.S3SecretKey), config);
    }

    public async Task<StoredObject> SaveAsync(
        Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);

        // 以年月分層，並加上一段亂碼避免同名覆蓋。
        // Path.GetFileName 是必要的：上傳來的檔名可能夾帶 ../ 之類的路徑片段。
        var safeName = Path.GetFileName(fileName);
        var key = $"{DateTime.UtcNow:yyyyMM}/{Guid.NewGuid():N}_{safeName}";

        // 長度要先取得：S3 需要 Content-Length，而 PutObject 對不可 seek 的
        // 串流會整份讀進記憶體去算。這裡的來源是檔案串流，本來就知道長度。
        var size = content.CanSeek ? content.Length : -1;

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            ContentType = string.IsNullOrWhiteSpace(contentType)
                ? "application/octet-stream"
                : contentType,
            AutoCloseStream = false,
            DisablePayloadSigning = true
        }, ct);

        _logger.LogInformation(
            "已保存原始檔 {FileName}（{Size:N0} bytes）→ s3://{Bucket}/{Key}",
            safeName, size, _bucket, key);

        return new StoredObject
        {
            Key = key,
            FileName = safeName,
            SizeBytes = size,
            ContentType = contentType,
            Location = $"s3://{_bucket}/{key}"
        };
    }

    public async Task<Stream?> OpenAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            var response = await _s3.GetObjectAsync(_bucket, key, ct);

            // 回應本身握著 HTTP 連線，只回傳 ResponseStream 會讓呼叫端
            // 無從釋放它。包一層，讓關閉串流的同時把回應一起關掉。
            return new ResponseBackedStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        try
        {
            await _s3.DeleteObjectAsync(_bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// 確保 bucket 存在。
    ///
    /// 放在第一次寫入時做而非啟動時做，是為了讓 SeaweedFS 尚未就緒時
    /// 後端仍然啟動得起來——問答與檢索不依賴物件儲存，不該被它拖住。
    /// </summary>
    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketReady) return;

        await _bucketGate.WaitAsync(ct);
        try
        {
            if (_bucketReady) return;

            try
            {
                await _s3.GetBucketLocationAsync(_bucket, ct);
            }
            catch (AmazonS3Exception ex) when (
                ex.StatusCode == System.Net.HttpStatusCode.NotFound ||
                ex.ErrorCode == "NoSuchBucket")
            {
                _logger.LogInformation("bucket {Bucket} 不存在，建立中", _bucket);
                await _s3.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, ct);
            }

            _bucketReady = true;
        }
        finally
        {
            _bucketGate.Release();
        }
    }

    /// <summary>
    /// 把 S3 回應的生命週期綁到串流上：關閉串流時一併釋放回應所持有的連線。
    /// </summary>
    private sealed class ResponseBackedStream : Stream
    {
        private readonly Stream _inner;
        private readonly IDisposable _response;

        public ResponseBackedStream(Stream inner, IDisposable response)
        {
            _inner = inner;
            _response = response;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken ct)
            => _inner.ReadAsync(buffer, offset, count, ct);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
            => _inner.ReadAsync(buffer, ct);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() => _inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
