using System.Net;

namespace Mosaic.Server.Tests;

public sealed class BackupTests
{
    private static object TwoItemBackup(string deviceId = "device-1") => new
    {
        deviceId,
        items = new object[]
        {
            new { kind = "task", key = "task-1", revision = 3, payload = new { title = "写周报", done = false } },
            new { kind = "setting", key = "theme", revision = 1, payload = new { mode = "dark" } },
        },
    };

    [Fact]
    public async Task Upload_latest_and_download_round_trip()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var upload = await host.PutAsync("/v1/backups", TwoItemBackup(), token);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var result = await TestServerHost.JsonAsync(upload);
        var snapshotId = result.GetProperty("snapshotId").GetString()!;
        Assert.Equal(2, result.GetProperty("itemCount").GetInt32());
        Assert.False(result.GetProperty("duplicate").GetBoolean());
        Assert.Equal(64, result.GetProperty("contentSha256").GetString()!.Length);

        var latest = await TestServerHost.JsonAsync(await host.GetAsync("/v1/backups/latest", token));
        Assert.Equal(snapshotId, latest.GetProperty("snapshotId").GetString());
        Assert.Equal(2, latest.GetProperty("items").GetArrayLength());
        Assert.False(latest.GetProperty("items")[0].TryGetProperty("payload", out _));

        var download = await TestServerHost.JsonAsync(await host.GetAsync($"/v1/backups/{snapshotId}", token));
        var task = download.GetProperty("items").EnumerateArray()
            .First(item => item.GetProperty("kind").GetString() == "task");
        Assert.Equal("写周报", task.GetProperty("payload").GetProperty("title").GetString());
        Assert.Equal(3, task.GetProperty("revision").GetInt64());

        var restore = await host.PostAsync($"/v1/backups/{snapshotId}/restore", new { }, token);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal(snapshotId, (await TestServerHost.JsonAsync(restore)).GetProperty("snapshotId").GetString());
    }

    [Fact]
    public async Task Identical_content_is_deduplicated()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var first = await TestServerHost.JsonAsync(await host.PutAsync("/v1/backups", TwoItemBackup(), token));
        var secondResponse = await host.PutAsync("/v1/backups", TwoItemBackup("device-2"), token);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await TestServerHost.JsonAsync(secondResponse);
        Assert.True(second.GetProperty("duplicate").GetBoolean());
        Assert.Equal(first.GetProperty("snapshotId").GetString(), second.GetProperty("snapshotId").GetString());

        var latest = await TestServerHost.JsonAsync(await host.GetAsync("/v1/backups/latest", token));
        Assert.Equal(first.GetProperty("snapshotId").GetString(), latest.GetProperty("snapshotId").GetString());
    }

    [Fact]
    public async Task Snapshots_are_isolated_between_users()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, owner) = await host.RegisterAsync();
        var ownerToken = TestServerHost.AccessToken(owner);
        var snapshot = await TestServerHost.JsonAsync(
            await host.PutAsync("/v1/backups", TwoItemBackup(), ownerToken));
        var snapshotId = snapshot.GetProperty("snapshotId").GetString()!;

        var (_, _, intruder) = await host.RegisterAsync();
        var intruderToken = TestServerHost.AccessToken(intruder);

        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync($"/v1/backups/{snapshotId}", intruderToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/v1/backups/latest", intruderToken)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await host.PostAsync($"/v1/backups/{snapshotId}/restore", new { }, intruderToken)).StatusCode);

        var badBase = await host.PutAsync("/v1/backups", new
        {
            deviceId = "device-1",
            baseSnapshotId = snapshotId,
            items = new[] { new { kind = "task", key = "task-9", revision = 1, payload = new { title = "x" } } },
        }, intruderToken);
        Assert.Equal(HttpStatusCode.NotFound, badBase.StatusCode);
    }

    [Fact]
    public async Task Quota_is_enforced()
    {
        await using var host = await TestServerHost.StartAsync(options => options.BackupQuotaBytes = 2048);
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var response = await host.PutAsync("/v1/backups", new
        {
            deviceId = "device-1",
            items = new object[]
            {
                new { kind = "task", key = "big", revision = 1, payload = new { blob = new string('x', 4096) } },
            },
        }, token);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("backup.quota_exceeded", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(response)));
    }

    [Fact]
    public async Task Content_hash_mismatch_is_rejected()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var response = await host.PutAsync("/v1/backups", new
        {
            deviceId = "device-1",
            contentSha256 = new string('a', 64),
            items = new[] { new { kind = "task", key = "task-1", revision = 1, payload = new { title = "x" } } },
        }, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("backup.hash_mismatch", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(response)));
    }

    [Fact]
    public async Task Invalid_backup_items_are_rejected()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var duplicate = await host.PutAsync("/v1/backups", new
        {
            deviceId = "device-1",
            items = new object[]
            {
                new { kind = "task", key = "same", revision = 1, payload = new { a = 1 } },
                new { kind = "task", key = "same", revision = 2, payload = new { a = 2 } },
            },
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        var badKind = await host.PutAsync("/v1/backups", new
        {
            deviceId = "device-1",
            items = new[] { new { kind = "note", key = "k", revision = 1, payload = new { a = 1 } } },
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);

        var badPayload = await host.PutAsync("/v1/backups", new
        {
            deviceId = "device-1",
            items = new[] { new { kind = "task", key = "k", revision = 1, payload = "text" } },
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, badPayload.StatusCode);

        var noDevice = await host.PutAsync("/v1/backups", new
        {
            items = new[] { new { kind = "task", key = "k", revision = 1, payload = new { a = 1 } } },
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, noDevice.StatusCode);
    }

    [Fact]
    public async Task Backups_require_authentication()
    {
        await using var host = await TestServerHost.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PutAsync("/v1/backups", TwoItemBackup(), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync("/v1/backups/latest")).StatusCode);
    }
}
