using System.Text.Json;
using Mosaic.Sync.Contracts;
using Mosaic.Sync.Http;

namespace Mosaic.Auth.Http.Tests;

public sealed class HttpSyncTransportTests
{
    private const string SnapshotJson =
        """{"snapshotId":"snap-1","createdAt":"2026-11-19T08:00:00Z","itemCount":2,"contentSha256":"sha-1","sizeBytes":2048}""";

    private static HttpSyncTransport Transport(bool allowLoopback = true) =>
        new(new HttpClient(), clock: null, allowInsecureLoopback: allowLoopback);

    [Fact]
    public async Task Upload_requires_https_and_rejects_plain_http_remotes()
    {
        await using var server = new LoopbackServer();

        var result = await Transport(allowLoopback: false).UploadAsync(
            server.BaseUri, "access-1", Guid.NewGuid(), [new(SyncItemKinds.Task, "k", "{}", 1)]);

        Assert.False(result.IsSuccess);
        Assert.Equal(SyncErrorCodes.ServerUntrusted, result.Error!.Code);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Upload_sends_the_device_identity_and_object_items()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (200, SnapshotJson);

        var result = await Transport().UploadAsync(
            server.BaseUri, "access-1", Guid.Parse("22222222-2222-2222-2222-222222222222"),
            [
                new(SyncItemKinds.Task, "33333333-3333-3333-3333-333333333333", """{"id":"33333333-3333-3333-3333-333333333333"}""", 7),
                new(SyncItemKinds.Setting, "appearance.theme", """{"value":"Dark"}""", 1)
            ]);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("snap-1", result.Value.SnapshotId);
        Assert.Equal(2, result.Value.ItemCount);
        var request = Assert.Single(server.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("/v1/backups", request.Path);
        Assert.Equal("Bearer access-1", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("22222222222222222222222222222222", body.RootElement.GetProperty("deviceId").GetString());
        var items = body.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("task", items[0].GetProperty("kind").GetString());
        Assert.Equal(7, items[0].GetProperty("revision").GetInt64());
        Assert.Equal("Dark", items[1].GetProperty("payload").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Upload_refuses_payloads_that_are_not_json_objects()
    {
        await using var server = new LoopbackServer();

        var notJson = await Transport().UploadAsync(server.BaseUri, "access-1", Guid.NewGuid(), [new(SyncItemKinds.Task, "k", "{oops", 1)]);
        var notObject = await Transport().UploadAsync(server.BaseUri, "access-1", Guid.NewGuid(), [new(SyncItemKinds.Task, "k", "[1,2]", 1)]);

        Assert.Equal(SyncErrorCodes.SnapshotInvalid, notJson.Error!.Code);
        Assert.Equal(SyncErrorCodes.SnapshotInvalid, notObject.Error!.Code);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Latest_returns_null_when_the_server_has_no_backup()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (404, """{"code":"backup.not_found","message":"还没有备份。"}""");

        var result = await Transport().GetLatestAsync(server.BaseUri, "access-1");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Null(result.Value);
        Assert.Equal("/v1/backups/latest", Assert.Single(server.Requests).Path);
    }

    [Fact]
    public async Task Latest_returns_the_snapshot_metadata()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (200, SnapshotJson);

        var result = await Transport().GetLatestAsync(server.BaseUri, "access-1");

        Assert.True(result.IsSuccess);
        Assert.Equal("snap-1", result.Value!.SnapshotId);
        Assert.Equal(2, result.Value.ItemCount);
        Assert.Equal(2048, result.Value.TotalBytes);
    }

    [Fact]
    public async Task Download_escapes_the_snapshot_id_and_reads_items()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (200, $$"""{"snapshotId":"snap 1","items":[{"kind":"task","key":"k","payload":{"id":"1"},"revision":3}]}""");

        var result = await Transport().DownloadAsync(server.BaseUri, "access-1", "snap 1");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("/v1/backups/snap%201", Assert.Single(server.Requests).Path);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal(SyncItemKinds.Task, item.Kind);
        Assert.Equal(3, item.Revision);
        Assert.Contains("\"id\"", item.Payload);
    }

    [Fact]
    public async Task Unauthorized_responses_map_to_not_signed_in()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (401, """{"code":"auth.required"}""");

        var result = await Transport().DownloadAsync(server.BaseUri, "expired", "snap-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(SyncErrorCodes.NotSignedIn, result.Error!.Code);
    }

    [Fact]
    public async Task Rate_limiting_is_reported_distinctly()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (429, "");

        var result = await Transport().GetLatestAsync(server.BaseUri, "access-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("SYNC_RATE_LIMITED", result.Error!.Code);
    }

    [Fact]
    public async Task Invalid_snapshot_responses_are_rejected()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (200, """{"createdAt":"2026-11-19T08:00:00Z"}""");

        var result = await Transport().GetLatestAsync(server.BaseUri, "access-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(SyncErrorCodes.SnapshotInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task Unreachable_server_reports_network_unavailable()
    {
        var result = await Transport().GetLatestAsync(LoopbackServer.ClosedUri(), "access-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(SyncErrorCodes.NetworkUnavailable, result.Error!.Code);
    }
}
