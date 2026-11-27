using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Mobile.Core;
using Mosaic.Mobile.Core.Api;
using Mosaic.Mobile.Core.Auth;
using Mosaic.Mobile.Core.Cards;
using Mosaic.Mobile.Core.Configuration;
using Mosaic.Mobile.Core.Projection;
using Mosaic.Mobile.Core.Sync;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Tests;

internal sealed record RecordedRequest(string Method, string Path, object? Body);

internal sealed class FakeMosaicApi : IMosaicApi
{
    public List<RecordedRequest> Requests { get; } = [];

    public Func<string, string, object?, object> Responder { get; set; } =
        (method, path, _) => throw new InvalidOperationException($"No response configured for {method} {path}.");

    public Task<ApiResponse<T>> GetAsync<T>(string path, string? accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(Respond<T>("GET", path, null));

    public Task<ApiResponse<T>> PostAsync<T>(string path, object body, string? accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(Respond<T>("POST", path, body));

    public Task<ApiResponse<byte[]>> GetBytesAsync(string path, long maxBytes, string? accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(Respond<byte[]>("GET-BYTES", path, null));

    private ApiResponse<T> Respond<T>(string method, string path, object? body)
    {
        Requests.Add(new RecordedRequest(method, path, body));
        return (ApiResponse<T>)Responder(method, path, body);
    }

    public static ApiResponse<T> Ok<T>(T value) => ApiResponse<T>.From(200, value, null);

    public static ApiResponse<T> Failure<T>(int status, string code, JsonElement? details = null) =>
        ApiResponse<T>.From(status, default, new WireError(code, null, null, details));

    public static ApiResponse<T> Transport<T>() => ApiResponse<T>.TransportFailure();

    public static SyncChangeDto AppliedChange(ChangePushRequestDto request, long serverRevision, bool replayed = false) =>
        new()
        {
            Kind = request.Kind,
            Key = request.Key,
            ServerRevision = serverRevision,
            Deleted = false,
            Payload = request.Payload,
            Cursor = serverRevision.ToString(CultureInfo.InvariantCulture),
        };

    public static ApiResponse<ChangePushResponseDto> Applied(ChangePushRequestDto request, bool replayed = false) =>
        Ok(new ChangePushResponseDto
        {
            Change = AppliedChange(request, request.BaseServerRevision + 1, replayed),
            Replayed = replayed,
        });
}

internal sealed class FakeApiFactory : IMosaicApiFactory
{
    private readonly FakeMosaicApi _api;

    public FakeApiFactory(FakeMosaicApi api)
    {
        _api = api;
    }

    public IMosaicApi Create(Uri baseUri) => _api;
}

internal sealed class RecordingTokenSource : IAccessTokenSource
{
    public string? Token { get; set; } = "token";

    public bool Invalidated { get; private set; }

    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult(Token);

    public Task InvalidateAsync(CancellationToken cancellationToken)
    {
        Invalidated = true;
        Token = null;
        return Task.CompletedTask;
    }
}

internal sealed class FixedServerConfigProvider : IServerConfigProvider
{
    public ServerConfiguration? Configuration { get; set; } = CreateConfiguration();

    public Task<ServerConfiguration?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Configuration);

    public static ServerConfiguration CreateConfiguration(string url = "https://mosaic.test/")
    {
        Assert.True(Mosaic.Mobile.Core.Configuration.ServerAddress.TryNormalize(url, out var configuration));
        return configuration!;
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mosaic-mobile-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Sample
{
    public static readonly Guid TaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static string Payload(
        Guid id,
        long revision = 2,
        string title = "整理周报",
        bool isCompleted = false,
        DateTimeOffset? archivedAt = null,
        DateTimeOffset? completedAt = null)
    {
        var node = new JsonObject
        {
            ["id"] = id.ToString("D"),
            ["title"] = title,
            ["notes"] = "不应出现在卡片上的备注",
            ["isCompleted"] = isCompleted,
            ["createdAtUtc"] = "2026-11-20T01:02:03Z",
            ["updatedAtUtc"] = "2026-11-25T01:02:03Z",
            ["revision"] = revision,
            ["completedAtUtc"] = completedAt is { } completed ? TaskPayload.Format(completed) : null,
            ["archivedAtUtc"] = archivedAt is { } archived ? TaskPayload.Format(archived) : null,
            ["restoredAtUtc"] = null,
            ["attachmentCount"] = 2,
        };
        return node.ToJsonString();
    }

    public static SyncedTask Task(Guid id, string payload, long serverRevision = 0)
    {
        Assert.True(TaskPayload.TryRead(payload, out var fields, out var error), error);
        return new SyncedTask
        {
            Id = id,
            Title = fields.Title,
            PayloadJson = payload,
            Revision = fields.Revision,
            ServerRevision = serverRevision,
            IsCompleted = fields.IsCompleted,
            ArchivedAtUtc = fields.ArchivedAtUtc,
            CreatedAtUtc = fields.CreatedAtUtc,
        };
    }

    public static TaskProjection SeededProjection(
        string directory,
        ProjectionIdentity identity,
        params SyncedTask[] tasks)
    {
        var open = TaskProjection.OpenAsync(identity, directory, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(open.IsOpened, open.Detail);
        var projection = open.Projection!;
        projection
            .ReplaceAsync(
                tasks,
                new ProjectionBaseline("snapshot-1", 42, DateTimeOffset.Parse("2026-11-25T00:00:00Z", CultureInfo.InvariantCulture), "7", DateTimeOffset.UtcNow),
                CancellationToken.None)
            .GetAwaiter().GetResult();
        return projection;
    }

    public static BackupItemDto BackupItem(Guid id, string payload, long revision = 2) =>
        new()
        {
            Kind = SyncKinds.Task,
            Key = id.ToString("D"),
            Revision = revision,
            Payload = JsonDocument.Parse(payload).RootElement.Clone(),
        };

    public static BackupSnapshotDto Snapshot(IReadOnlyList<BackupItemDto> items, string snapshotId = "snapshot-1") =>
        new()
        {
            SnapshotId = snapshotId,
            CreatedAt = DateTimeOffset.Parse("2026-11-25T00:00:00Z", CultureInfo.InvariantCulture),
            ContentSha256 = BackupCanonicalizer.ComputeSha256Hex(items),
            ItemCount = items.Count,
            ServerRevision = 42,
            Cursor = "7",
            Items = [.. items],
        };

    public static BackupManifestDto Manifest(BackupSnapshotDto snapshot) =>
        new()
        {
            SnapshotId = snapshot.SnapshotId,
            CreatedAt = snapshot.CreatedAt,
            ContentSha256 = snapshot.ContentSha256,
            ItemCount = snapshot.ItemCount,
            ServerRevision = snapshot.ServerRevision,
            Cursor = snapshot.Cursor,
        };

    public static JsonElement Details(string json) => JsonDocument.Parse(json).RootElement.Clone();
}