using System.Net;
using System.Text.Json;

namespace Mosaic.Server.Tests;

public sealed class SyncTests
{
    private static object Push(
        string key,
        long baseRevision,
        object? payload = null,
        string kind = "task",
        string? idempotencyKey = null,
        bool deleted = false,
        string deviceId = "device-1") => new
    {
        deviceId,
        kind,
        key,
        payload = deleted ? null : payload ?? new { title = key },
        deleted,
        baseServerRevision = baseRevision,
        clientRevision = 1,
        idempotencyKey = idempotencyKey ?? Guid.NewGuid().ToString("N"),
    };

    private static async Task<JsonElement> PushAsync(TestServerHost host, string token, object body)
    {
        var response = await host.PostAsync("/v1/sync/changes", body, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TestServerHost.JsonAsync(response);
    }

    [Fact]
    public async Task Push_and_pull_round_trip_with_cursor()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var first = await PushAsync(host, token, Push("task-1", 0, new { title = "A" }));
        Assert.Equal(1, first.GetProperty("change").GetProperty("serverRevision").GetInt64());
        Assert.False(first.GetProperty("replayed").GetBoolean());
        var second = await PushAsync(host, token, Push("task-2", 0, new { title = "B" }));
        Assert.Equal(2, second.GetProperty("change").GetProperty("serverRevision").GetInt64());

        var pull = await TestServerHost.JsonAsync(await host.GetAsync("/v1/sync/changes?cursor=0&limit=100", token));
        Assert.Equal(2, pull.GetProperty("changes").GetArrayLength());
        Assert.Equal("2", pull.GetProperty("nextCursor").GetString());
        Assert.False(pull.GetProperty("hasMore").GetBoolean());
        Assert.Equal("task-1", pull.GetProperty("changes")[0].GetProperty("key").GetString());

        var empty = await TestServerHost.JsonAsync(await host.GetAsync("/v1/sync/changes?cursor=2", token));
        Assert.Equal(0, empty.GetProperty("changes").GetArrayLength());
        Assert.Equal("2", empty.GetProperty("nextCursor").GetString());
    }

    [Fact]
    public async Task Stale_base_revision_returns_a_conflict_with_server_state()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        await PushAsync(host, token, Push("task-1", 0, new { title = "A" }));

        var conflict = await host.PostAsync("/v1/sync/changes", Push("task-1", 0, new { title = "B" }), token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await TestServerHost.JsonAsync(conflict);
        Assert.Equal("sync.conflict", TestServerHost.ErrorCode(body));
        var current = body.GetProperty("current");
        Assert.Equal(1, current.GetProperty("serverRevision").GetInt64());
        Assert.Equal("A", current.GetProperty("payload").GetProperty("title").GetString());

        var pull = await TestServerHost.JsonAsync(await host.GetAsync("/v1/sync/changes", token));
        Assert.Equal(1, pull.GetProperty("changes").GetArrayLength());

        var retry = await PushAsync(host, token, Push("task-1", 1, new { title = "B" }));
        Assert.Equal(2, retry.GetProperty("change").GetProperty("serverRevision").GetInt64());
    }

    [Fact]
    public async Task Conflicts_can_report_a_missing_server_item()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var response = await host.PostAsync("/v1/sync/changes", Push("ghost", 5), token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await TestServerHost.JsonAsync(response);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("current").ValueKind);
    }

    [Fact]
    public async Task Tombstones_are_propagated_and_carry_no_payload()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        await PushAsync(host, token, Push("task-1", 0, new { title = "A" }));
        var deleted = await PushAsync(host, token, Push("task-1", 1, deleted: true));
        Assert.True(deleted.GetProperty("change").GetProperty("deleted").GetBoolean());
        Assert.Equal(2, deleted.GetProperty("change").GetProperty("serverRevision").GetInt64());

        var pull = await TestServerHost.JsonAsync(await host.GetAsync("/v1/sync/changes?cursor=0", token));
        var changes = pull.GetProperty("changes");
        Assert.Equal(2, changes.GetArrayLength());
        var tombstone = changes[1];
        Assert.True(tombstone.GetProperty("deleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, tombstone.GetProperty("payload").ValueKind);
    }

    [Fact]
    public async Task Idempotency_key_replays_the_same_change()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var body = Push("task-1", 0, new { title = "A" }, idempotencyKey: "fixed-key");
        var first = await PushAsync(host, token, body);
        var second = await PushAsync(host, token, body);

        Assert.False(first.GetProperty("replayed").GetBoolean());
        Assert.True(second.GetProperty("replayed").GetBoolean());
        Assert.Equal(
            first.GetProperty("change").GetProperty("cursor").GetString(),
            second.GetProperty("change").GetProperty("cursor").GetString());

        var pull = await TestServerHost.JsonAsync(await host.GetAsync("/v1/sync/changes", token));
        Assert.Equal(1, pull.GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task Users_and_devices_do_not_see_each_others_changes()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, first) = await host.RegisterAsync();
        var firstToken = TestServerHost.AccessToken(first);
        await PushAsync(host, firstToken, Push("task-1", 0, new { title = "A" }));

        var (_, _, other) = await host.RegisterAsync();
        var otherToken = TestServerHost.AccessToken(other);
        var pull = await TestServerHost.JsonAsync(await host.GetAsync("/v1/sync/changes?cursor=0", otherToken));
        Assert.Equal(0, pull.GetProperty("changes").GetArrayLength());
        Assert.Equal("0", pull.GetProperty("nextCursor").GetString());
    }

    [Fact]
    public async Task Invalid_requests_are_rejected()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var badKind = await host.PostAsync("/v1/sync/changes", new
        {
            deviceId = "device-1",
            kind = "note",
            key = "k",
            payload = new { a = 1 },
            deleted = false,
            baseServerRevision = 0,
            idempotencyKey = "k1",
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);

        var noKey = await host.PostAsync("/v1/sync/changes", new
        {
            deviceId = "device-1",
            kind = "task",
            key = "k",
            payload = new { a = 1 },
            deleted = false,
            baseServerRevision = 0,
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);

        var badCursor = await host.GetAsync("/v1/sync/changes?cursor=abc", token);
        Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);

        var badLimit = await host.GetAsync("/v1/sync/changes?limit=1000", token);
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
    }

    [Fact]
    public async Task Setting_changes_are_limited_to_the_portable_whitelist()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var allowed = await PushAsync(host, token, Push(
            "ui.language", 0, new { value = "zh-CN" }, kind: "setting"));
        Assert.Equal("setting", allowed.GetProperty("change").GetProperty("kind").GetString());

        var rejected = await host.PostAsync("/v1/sync/changes", Push(
            "sync.secret", 0, new { value = "x" }, kind: "setting"), token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("sync.invalid", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(rejected)));
    }

    [Fact]
    public async Task Malformed_cursor_is_rejected_and_requires_a_snapshot()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var response = await host.GetAsync("/v1/sync/changes?cursor=not-a-number", token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await TestServerHost.JsonAsync(response);
        Assert.Equal("sync.invalid", TestServerHost.ErrorCode(error));
        Assert.True(error.GetProperty("details").GetProperty("requiresSnapshot").GetBoolean());
    }

    [Fact]
    public async Task Pruned_history_answers_cursor_invalid_requiring_a_snapshot()
    {
        await using var host = await TestServerHost.StartAsync(options => options.SyncHistorySeconds = 1);
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        await PushAsync(host, token, Push("task-1", 0, new { title = "A" }));
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        await PushAsync(host, token, Push("task-2", 0, new { title = "B" }));

        var stale = await host.GetAsync("/v1/sync/changes?cursor=0", token);
        Assert.Equal(HttpStatusCode.Gone, stale.StatusCode);
        var error = await TestServerHost.JsonAsync(stale);
        Assert.Equal("sync.cursor_invalid", TestServerHost.ErrorCode(error));
        Assert.True(error.GetProperty("details").GetProperty("requiresSnapshot").GetBoolean());
        Assert.True(error.GetProperty("requiresSnapshot").GetBoolean());

        var resumed = await TestServerHost.JsonAsync(
            await host.GetAsync("/v1/sync/changes?cursor=1", token));
        Assert.Equal(1, resumed.GetProperty("changes").GetArrayLength());
        Assert.Equal("task-2", resumed.GetProperty("changes")[0].GetProperty("key").GetString());
        Assert.Equal("2", resumed.GetProperty("nextCursor").GetString());
    }

    [Fact]
    public async Task Pull_records_the_device_cursor()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);
        await PushAsync(host, token, Push("task-1", 0));

        var pull = await TestServerHost.JsonAsync(
            await host.GetAsync("/v1/sync/changes?cursor=0&deviceId=device-1", token));
        Assert.Equal("1", pull.GetProperty("nextCursor").GetString());

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=" + host.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cursor FROM sync_cursors";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }
}
