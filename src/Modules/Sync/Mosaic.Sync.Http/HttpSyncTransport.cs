using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
        snapshot = new(id, createdAt, itemCount, Text(root, "contentSha256") ?? "", bytes);
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
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return OperationResult<T>.Failure(ReadError(response.StatusCode, text));
            using var document = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
            return read(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return Invalid<T>("服务器响应格式无效。");
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
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

    private static OperationResult<T> Failure<T>(string code, string message) =>
        OperationResult<T>.Failure(new(code, message));

    private sealed record UploadBody(string DeviceId, string? BaseSnapshotId, string? ContentSha256, IReadOnlyList<UploadItem> Items);

    private sealed record UploadItem(string Kind, string Key, long Revision, JsonElement Payload);
}
