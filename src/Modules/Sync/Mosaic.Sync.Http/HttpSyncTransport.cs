using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Mosaic.Contracts;
using Mosaic.Sync.Contracts;

namespace Mosaic.Sync.Http;

/// <summary>
/// Wire adapter for the server's <c>/v1/backups</c> endpoints. Only the access token travels in
/// the Authorization header; item payloads are opaque JSON that never touch local logs.
/// </summary>
public sealed class HttpSyncTransport : ISyncTransport
{
    private const string BackupsPath = "v1/backups";
    private const string LatestPath = "v1/backups/latest";
    private const string AttachmentsPath = "v1/attachments";
    // The module sets HttpClient.Timeout to infinite: a single attachment may legitimately take
    // minutes, and a shared timeout would cut it off. Every request therefore carries its own
    // linked deadline instead of relying on the handler.
    private static readonly TimeSpan JsonRequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AttachmentRequestTimeout = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly TimeProvider _clock;
    private readonly bool _allowInsecureLoopback;

    public HttpSyncTransport(HttpClient client, TimeProvider? clock = null, bool allowInsecureLoopback = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _clock = clock ?? TimeProvider.System;
        _allowInsecureLoopback = allowInsecureLoopback;
    }

    public async Task<OperationResult<BackupSnapshotDto>> UploadAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        IReadOnlyList<BackupItemDto> items,
        CancellationToken cancellationToken = default)
    {
        var payloads = new List<UploadItem>(items.Count);
        foreach (var item in items)
        {
            JsonElement payload;
            try
            {
                payload = JsonSerializer.Deserialize<JsonElement>(item.Payload);
            }
            catch (JsonException)
            {
                return OperationResult<BackupSnapshotDto>.Failure(new(SyncErrorCodes.SnapshotInvalid, "备份内容无法读取。"));
            }
            if (payload.ValueKind != JsonValueKind.Object)
                return OperationResult<BackupSnapshotDto>.Failure(new(SyncErrorCodes.SnapshotInvalid, "备份条目必须是 JSON 对象。"));
            payloads.Add(new(item.Kind, item.Key, item.Revision, payload));
        }
        var body = new UploadBody(deviceId.ToString("N"), null, null, payloads);
        return await ReadAsync(HttpMethod.Put, server, BackupsPath, body, accessToken, ReadSnapshot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<BackupSnapshotDto?>> GetLatestAsync(Uri server, string accessToken, CancellationToken cancellationToken = default)
    {
        var latest = await ReadAsync(HttpMethod.Get, server, LatestPath, null, accessToken, ReadSnapshot, cancellationToken).ConfigureAwait(false);
        if (latest.IsSuccess) return OperationResult<BackupSnapshotDto?>.Success(latest.Value);
        if (string.Equals(latest.Error!.Code, "backup.not_found", StringComparison.Ordinal))
            return OperationResult<BackupSnapshotDto?>.Success(null);
        return OperationResult<BackupSnapshotDto?>.Failure(latest.Error);
    }

    public async Task<OperationResult<BackupSnapshotContentDto>> DownloadAsync(Uri server, string accessToken, string snapshotId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
            return OperationResult<BackupSnapshotContentDto>.Failure(new(SyncErrorCodes.SnapshotInvalid, "备份标识无效。"));
        var path = BackupsPath + "/" + Uri.EscapeDataString(snapshotId);
        return await ReadAsync(HttpMethod.Get, server, path, null, accessToken, root =>
        {
            if (!TryReadSnapshot(root, out var snapshot))
                return Invalid<BackupSnapshotContentDto>("服务器返回的备份信息无效。");
            var items = new List<BackupItemDto>();
            if (root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var kind = Text(item, "kind");
                    var key = Text(item, "key");
                    if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(key) ||
                        !item.TryGetProperty("payload", out var payload))
                        return Invalid<BackupSnapshotContentDto>("备份条目内容无效。");
                    var revision = item.TryGetProperty("revision", out var value) && value.TryGetInt64(out var parsed) ? parsed : 1;
                    items.Add(new(kind, key, payload.GetRawText(), revision));
                }
            }
            return OperationResult<BackupSnapshotContentDto>.Success(new(snapshot, items));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<AttachmentManifestDto>> ListAttachmentsAsync(Uri server, string accessToken, CancellationToken cancellationToken = default) =>
        await ReadAsync(HttpMethod.Get, server, AttachmentsPath, null, accessToken, ReadManifest, cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> UploadAttachmentAsync(
        Uri server, string accessToken, Guid taskId, Guid attachmentId, string fileName, Stream content, string sha256,
        CancellationToken cancellationToken = default)
    {
        if (!IsAllowedServer(server))
            return Failure(SyncErrorCodes.ServerUntrusted, "服务器地址必须使用 HTTPS。");
        if (content is null) return Failure(SyncErrorCodes.AttachmentsInvalid, "附件内容无效。");
        if (!Uri.TryCreate(server, AttachmentEndpoint(attachmentId, taskId, fileName), out var endpoint))
            return Failure(SyncErrorCodes.ServerUntrusted, "服务器地址无效。");

        using var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
        var body = new StreamContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // Content-Length 让服务器在读完前就能拒绝超限附件；不可 seek 的流交给分块传输。
        if (content.CanSeek) body.Headers.ContentLength = content.Length - content.Position;
        request.Content = body;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("X-Mosaic-Sha256", sha256);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(AttachmentRequestTimeout);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return OperationResult.Success();
            return OperationResult.Failure(ReadError(response.StatusCode, await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failure(SyncErrorCodes.NetworkUnavailable, "附件上传超时，请检查网络后重试。"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Failure(SyncErrorCodes.NetworkUnavailable, "无法连接服务器，请检查网络与服务器地址。");
        }
    }

    public async Task<OperationResult> DownloadAttachmentAsync(
        Uri server, string accessToken, Guid attachmentId, string targetPath, string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (!IsAllowedServer(server))
            return Failure(SyncErrorCodes.ServerUntrusted, "服务器地址必须使用 HTTPS。");
        if (!Uri.TryCreate(server, AttachmentEndpoint(attachmentId, null, null), out var endpoint))
            return Failure(SyncErrorCodes.ServerUntrusted, "服务器地址无效。");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(AttachmentRequestTimeout);
        FileStream? target = null;
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return OperationResult.Failure(ReadError(response.StatusCode, await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false)));

            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
            }
            await target.DisposeAsync().ConfigureAwait(false);
            target = null;

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (string.IsNullOrEmpty(expectedSha256) || string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                return OperationResult.Success();
            TryDelete(targetPath);
            return Failure(SyncErrorCodes.AttachmentHashMismatch, "附件内容校验失败，已丢弃下载内容。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DiscardAsync(target, targetPath).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await DiscardAsync(target, targetPath).ConfigureAwait(false);
            return Failure(SyncErrorCodes.NetworkUnavailable, "附件下载超时，请检查网络后重试。");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            await DiscardAsync(target, targetPath).ConfigureAwait(false);
            return Failure(SyncErrorCodes.NetworkUnavailable, "无法下载附件，请检查网络与附件目录。");
        }
    }

    public async Task<OperationResult> DeleteAttachmentAsync(Uri server, string accessToken, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedServer(server))
            return Failure(SyncErrorCodes.ServerUntrusted, "服务器地址必须使用 HTTPS。");
        if (!Uri.TryCreate(server, AttachmentEndpoint(attachmentId, null, null), out var endpoint))
            return Failure(SyncErrorCodes.ServerUntrusted, "服务器地址无效。");

        using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(JsonRequestTimeout);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            // 服务器对不存在（或属于别人）的附件同样回 204，这里把 404 也当成功，重复删除永远安全。
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return OperationResult.Success();
            return OperationResult.Failure(ReadError(response.StatusCode, await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failure(SyncErrorCodes.NetworkUnavailable, "服务器响应超时，请稍后再试。"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Failure(SyncErrorCodes.NetworkUnavailable, "无法连接服务器，请检查网络与服务器地址。");
        }
    }

    private OperationResult<AttachmentManifestDto> ReadManifest(JsonElement root)
    {
        var items = new List<RemoteAttachmentDto>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var id = Text(item, "id");
                var taskId = Text(item, "taskId");
                var fileName = Text(item, "fileName");
                if (!Guid.TryParseExact(id, "D", out var parsedId) || !Guid.TryParseExact(taskId, "D", out var parsedTaskId) || string.IsNullOrWhiteSpace(fileName))
                    return Invalid<AttachmentManifestDto>("服务器返回的附件清单无效。");
                var size = item.TryGetProperty("sizeBytes", out var value) && value.TryGetInt64(out var parsedSize) ? parsedSize : 0;
                var addedAt = DateTimeOffset.TryParse(Text(item, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedAdded)
                    ? parsedAdded
                    : _clock.GetUtcNow();
                items.Add(new(parsedId, parsedTaskId, fileName, size, Text(item, "sha256") ?? "", addedAt));
            }
        }
        var usage = root.TryGetProperty("usageBytes", out var usageValue) && usageValue.TryGetInt64(out var parsedUsage) ? parsedUsage : 0;
        var quota = root.TryGetProperty("quotaBytes", out var quotaValue) && quotaValue.TryGetInt64(out var parsedQuota) ? parsedQuota : 0;
        var max = root.TryGetProperty("maxBytes", out var maxValue) && maxValue.TryGetInt64(out var parsedMax) ? parsedMax : 0;
        return OperationResult<AttachmentManifestDto>.Success(new(items, usage, quota, max));
    }

    private static string AttachmentEndpoint(Guid attachmentId, Guid? taskId, string? fileName)
    {
        var path = AttachmentsPath + "/" + attachmentId.ToString("D");
        return taskId is { } task
            ? path + "?taskId=" + task.ToString("D") + "&fileName=" + Uri.EscapeDataString(fileName ?? "")
            : path;
    }

    private static async Task DiscardAsync(FileStream? target, string path)
    {
        if (target is not null)
        {
            try { await target.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }
        TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private OperationResult<BackupSnapshotDto> ReadSnapshot(JsonElement root) =>
        TryReadSnapshot(root, out var snapshot)
            ? OperationResult<BackupSnapshotDto>.Success(snapshot)
            : Invalid<BackupSnapshotDto>("服务器返回的备份信息无效。");

    private bool TryReadSnapshot(JsonElement root, out BackupSnapshotDto snapshot)
    {
        snapshot = null!;
        var id = Text(root, "snapshotId");
        if (string.IsNullOrWhiteSpace(id)) return false;
        var createdAt = DateTimeOffset.TryParse(Text(root, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : _clock.GetUtcNow();
        var itemCount = root.TryGetProperty("itemCount", out var count) && count.TryGetInt32(out var items) ? items : 0;
        var bytes = root.TryGetProperty("sizeBytes", out var size) && size.TryGetInt64(out var total) ? total : 0;
        var serverRevision = root.TryGetProperty("serverRevision", out var revision) && revision.TryGetInt64(out var parsedRevision) ? parsedRevision : 0;
        var taskCount = root.TryGetProperty("taskCount", out var tasks) && tasks.TryGetInt32(out var parsedTasks) ? parsedTasks : -1;
        snapshot = new(id, createdAt, itemCount, Text(root, "contentSha256") ?? "", bytes, serverRevision, Text(root, "cursor"), taskCount);
        return true;
    }

    private async Task<OperationResult<T>> ReadAsync<T>(
        HttpMethod method,
        Uri server,
        string path,
        object? body,
        string accessToken,
        Func<JsonElement, OperationResult<T>> read,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedServer(server))
            return Failure<T>(SyncErrorCodes.ServerUntrusted, "服务器地址必须使用 HTTPS。");
        if (!Uri.TryCreate(server, path, out var endpoint))
            return Failure<T>(SyncErrorCodes.ServerUntrusted, "服务器地址无效。");
        using var request = new HttpRequestMessage(method, endpoint);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        if (!string.IsNullOrEmpty(accessToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // 截止时间挂在每个请求上：调用方取消要原样抛出（上层据此判定"用户取消"），
        // 只有我们自己的截止时间触发才映射为网络超时。
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(JsonRequestTimeout);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return OperationResult<T>.Failure(ReadError(response.StatusCode, text));
            using var document = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
            return read(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure<T>(SyncErrorCodes.NetworkUnavailable, "服务器响应超时，请稍后再试。");
        }
        catch (JsonException)
        {
            return Invalid<T>("服务器响应格式无效。");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Failure<T>(SyncErrorCodes.NetworkUnavailable, "无法连接服务器，请检查网络与服务器地址。");
        }
    }

    private bool IsAllowedServer(Uri server) =>
        server.IsAbsoluteUri && (IsHttps(server) || (_allowInsecureLoopback && IsLoopback(server)));

    private static bool IsHttps(Uri server) =>
        string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(server.UserInfo) && string.IsNullOrEmpty(server.Query);

    private static bool IsLoopback(Uri server) =>
        string.Equals(server.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(server.Host, "127.0.0.1", StringComparison.Ordinal) ||
         string.Equals(server.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(server.Host, "::1", StringComparison.Ordinal));

    private static OperationError ReadError(HttpStatusCode status, string text)
    {
        string? code = null, message = null, traceId = null;
        if (text.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    code = Text(document.RootElement, "code");
                    message = Text(document.RootElement, "message");
                    traceId = Text(document.RootElement, "traceId");
                }
            }
            catch (JsonException) { }
        }
        var mapped = status switch
        {
            HttpStatusCode.Unauthorized => SyncErrorCodes.NotSignedIn,
            HttpStatusCode.TooManyRequests => "SYNC_RATE_LIMITED",
            _ => code ?? "SYNC_FAILED"
        };
        return new(mapped, message ?? FallbackMessage(mapped), CorrelationId: traceId);
    }

    private static string FallbackMessage(string code) => code switch
    {
        SyncErrorCodes.NotSignedIn => "登录已过期，请重新登录。",
        "SYNC_RATE_LIMITED" => "请求过于频繁，请稍后再试。",
        _ => "备份请求未完成，请稍后再试。"
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static OperationResult<T> Invalid<T>(string message) =>
        Failure<T>(SyncErrorCodes.SnapshotInvalid, message);

    private static OperationResult Failure(string code, string message) =>
        OperationResult.Failure(new(code, message));

    private static OperationResult<T> Failure<T>(string code, string message) =>
        OperationResult<T>.Failure(new(code, message));

    private sealed record UploadBody(string DeviceId, string? BaseSnapshotId, string? ContentSha256, IReadOnlyList<UploadItem> Items);

    private sealed record UploadItem(string Kind, string Key, long Revision, JsonElement Payload);
}
