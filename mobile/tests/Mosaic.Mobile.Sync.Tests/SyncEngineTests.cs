using System.Net;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync.Tests;

/// <summary>
/// End-to-end runs of <see cref="SyncEngine"/> over a real per-account SQLite
/// projection and a stubbed HTTP stack. Every page is canonical 0.1.1 JSON, so
/// the wire parse, the strict projector and the atomic page commit are all on
/// the path.
/// </summary>
public sealed class SyncEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static string Page(string changes, string cursor, bool hasMore) =>
        $$"""
          {"changes":[{{changes}}],"nextCursor":"{{cursor}}","hasMore":{{(hasMore ? "true" : "false")}}}
          """;

    private sealed record Fixture(
        SyncEngine Engine,
        TempDatabase Database,
        StubHandler Handler,
        TestTimeProvider Time) : IDisposable
    {
        public void Dispose() => Database.Dispose();
    }

    private static Fixture Create(Func<RecordedRequest, HttpResponseMessage> responder) =>
        Create(new StubHandler(responder));

    private static Fixture CreateAsync(Func<RecordedRequest, Task<HttpResponseMessage>> responder) =>
        Create(StubHandler.FromAsync(responder));

    private static Fixture Create(StubHandler handler)
    {
        var database = new TempDatabase();
        var api = new MosaicApiClient(new HttpClient(handler), (_, _) => Task.CompletedTask)
        {
            BaseUrl = "https://example.test",
        };
        var time = new TestTimeProvider();
        var engine = new SyncEngine(api, database.Store, new SnapshotBootstrapService(api), time);
        return new Fixture(engine, database, handler, time);
    }

    /// <summary>Puts the projection at a known cursor, as an earlier run would.</summary>
    private static Task SeedCursorAsync(TempDatabase database, string cursor) =>
        database.Store.ApplyPageAsync(new SyncPage([], [], cursor, false, Now, null));

    private static IEnumerable<RecordedRequest> ChangesCalls(Fixture fixture) =>
        fixture.Handler.Requests.Where(request =>
            request.Uri.Contains("/v1/sync/changes", StringComparison.Ordinal));

    private static HttpResponseMessage NoSnapshot() =>
        Responses.Error(HttpStatusCode.NotFound, WireErrorCodes.BackupNotFound);

    [Fact]
    public async Task First_run_without_a_snapshot_pulls_from_the_beginning()
    {
        using var fixture = Create(request =>
            request.Uri.Contains("/v1/backups/latest", StringComparison.Ordinal)
                ? NoSnapshot()
                : Responses.Changes(Page(
                    TaskJson.Change(Ids.One, "First", serverRevision: 1),
                    "c1",
                    hasMore: false)));

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.True(report.Succeeded);
        Assert.False(report.Bootstrapped);
        Assert.Equal(1, report.Pages);
        Assert.Equal(1, report.Changes);
        Assert.Equal(SyncStatusType.Ready, fixture.Engine.Status.Status);
        Assert.Equal("First", (await fixture.Database.Store.GetTaskAsync(Guid.Parse(Ids.One)))?.Title);
        Assert.Equal("c1", (await fixture.Database.Store.GetStateAsync()).Cursor);

        var changes = Assert.Single(ChangesCalls(fixture));
        Assert.DoesNotContain("cursor=", changes.Uri, StringComparison.Ordinal);
        Assert.Contains($"limit={SyncEngine.PageSize}", changes.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_first_run_is_reported_as_empty()
    {
        using var fixture = Create(request =>
            request.Uri.Contains("/v1/backups/latest", StringComparison.Ordinal)
                ? NoSnapshot()
                : Responses.Changes(Page(string.Empty, "c1", hasMore: false)));

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(0, report.Changes);
        Assert.Equal(SyncStatusType.Empty, fixture.Engine.Status.Status);
        Assert.NotNull(report.LastCompleteSyncAt);
        Assert.Equal("c1", (await fixture.Database.Store.GetStateAsync()).Cursor);
    }

    [Fact]
    public async Task Two_pages_are_applied_and_the_cursor_tracks_the_last_page()
    {
        using var fixture = Create(request =>
        {
            if (request.Uri.Contains("/v1/backups/latest", StringComparison.Ordinal))
            {
                return NoSnapshot();
            }

            return request.Uri.Contains("cursor=c1", StringComparison.Ordinal)
                ? Responses.Changes(Page(
                    TaskJson.Change(Ids.Two, "Second", serverRevision: 2),
                    "c2",
                    hasMore: false))
                : Responses.Changes(Page(
                    TaskJson.Change(Ids.One, "First", serverRevision: 1)
                    + ","
                    + TaskJson.Setting("ui.language", "\"English\"", serverRevision: 2),
                    "c1",
                    hasMore: true));
        });

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.Pages);
        Assert.Equal(2, report.Changes);
        Assert.Equal(2, await fixture.Database.Store.CountTasksAsync());
        Assert.Equal("c2", (await fixture.Database.Store.GetStateAsync()).Cursor);
        Assert.NotNull(report.LastCompleteSyncAt);
        Assert.Equal(SyncStatusType.Ready, fixture.Engine.Status.Status);
    }

    [Fact]
    public async Task First_run_restores_the_latest_snapshot_before_pulling()
    {
        var body = SnapshotJson.Body(
            SnapshotJson.Id,
            SnapshotJson.Cursor,
            40,
            (Ids.One, "From snapshot"),
            (Ids.Two, "Also from snapshot"));

        using var fixture = Create(request =>
        {
            if (request.Uri.Contains("/v1/backups/latest", StringComparison.Ordinal))
            {
                return Responses.Json(HttpStatusCode.OK, SnapshotJson.Manifest(body, itemCount: 2));
            }

            if (request.Uri.Contains($"/v1/backups/{SnapshotJson.Id}", StringComparison.Ordinal))
            {
                return Responses.Bytes(HttpStatusCode.OK, body);
            }

            return Responses.Changes(Page(string.Empty, "c-after", hasMore: false));
        });

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.True(report.Succeeded);
        Assert.True(report.Bootstrapped);
        Assert.Equal(2, report.Changes);
        Assert.Equal(2, await fixture.Database.Store.CountTasksAsync());
        Assert.Equal("From snapshot", (await fixture.Database.Store.GetTaskAsync(Guid.Parse(Ids.One)))?.Title);
        Assert.Equal("c-after", (await fixture.Database.Store.GetStateAsync()).Cursor);

        var changes = Assert.Single(ChangesCalls(fixture));
        Assert.Contains(
            $"cursor={Uri.EscapeDataString(SnapshotJson.Cursor)}",
            changes.Uri,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_stops_after_twenty_pages_and_is_incomplete()
    {
        var page = 0;
        using var fixture = Create(request =>
            request.Uri.Contains("/v1/backups/latest", StringComparison.Ordinal)
                ? NoSnapshot()
                : Responses.Changes(Page(
                    TaskJson.Change(Ids.One, "Task", serverRevision: 1),
                    $"c{++page}",
                    hasMore: true)));

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.True(report.Succeeded);
        Assert.True(report.Incomplete);
        Assert.Equal(SyncEngine.MaxPagesPerRun, report.Pages);
        Assert.Equal(SyncEngine.MaxPagesPerRun, ChangesCalls(fixture).Count());
        Assert.Null(report.LastCompleteSyncAt);
        Assert.Equal(SyncStatusType.Incomplete, fixture.Engine.Status.Status);
        Assert.Equal(
            ProjectionFreshness.Syncing,
            (await fixture.Database.Store.GetStateAsync()).Freshness);
    }

    [Fact]
    public async Task Unauthorized_marks_needs_sign_in()
    {
        using var fixture = Create(_ =>
            Responses.Error(HttpStatusCode.Unauthorized, WireErrorCodes.AuthSessionExpired));

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(SyncErrorKind.SessionExpired, report.Error?.Kind);
        Assert.Equal(SyncStatusType.NeedsSignIn, fixture.Engine.Status.Status);
        Assert.Null((await fixture.Database.Store.GetStateAsync()).Cursor);
    }

    [Fact]
    public async Task Cursor_invalid_restores_the_snapshot_then_continues()
    {
        var changesCalls = 0;
        var body = SnapshotJson.Body(
            SnapshotJson.Id,
            SnapshotJson.Cursor,
            40,
            (Ids.One, "From snapshot"));

        using var fixture = Create(request =>
        {
            if (request.Uri.Contains("/v1/sync/changes", StringComparison.Ordinal))
            {
                changesCalls++;
                return changesCalls == 1
                    ? Responses.Error(HttpStatusCode.Gone, WireErrorCodes.SyncCursorInvalid)
                    : Responses.Changes(Page(string.Empty, "c9", hasMore: false));
            }

            if (request.Uri.Contains("/v1/backups/latest", StringComparison.Ordinal))
            {
                return Responses.Json(HttpStatusCode.OK, SnapshotJson.Manifest(body));
            }

            return request.Uri.Contains($"/v1/backups/{SnapshotJson.Id}", StringComparison.Ordinal)
                ? Responses.Bytes(HttpStatusCode.OK, body)
                : Responses.Error(HttpStatusCode.NotFound, "server.not_found");
        });

        await SeedCursorAsync(fixture.Database, "c-before");

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.True(report.Succeeded);
        Assert.True(report.Bootstrapped);
        Assert.Equal(2, changesCalls);
        Assert.Equal("From snapshot", (await fixture.Database.Store.GetTaskAsync(Guid.Parse(Ids.One)))?.Title);
        Assert.Equal("c9", (await fixture.Database.Store.GetStateAsync()).Cursor);
        Assert.Equal(SyncStatusType.Ready, fixture.Engine.Status.Status);
    }

    [Fact]
    public async Task Malformed_cursor_with_requires_snapshot_bootstraps()
    {
        var changesCalls = 0;
        var body = SnapshotJson.Body(
            SnapshotJson.Id,
            SnapshotJson.Cursor,
            40,
            (Ids.Two, "Recovered"));

        using var fixture = Create(request =>
        {
            if (request.Uri.Contains("/v1/sync/changes", StringComparison.Ordinal))
            {
                changesCalls++;
                return changesCalls == 1
                    ? Responses.Error(
                        HttpStatusCode.BadRequest,
                        WireErrorCodes.SyncInvalid,
                        new { requiresSnapshot = true })
                    : Responses.Changes(Page(string.Empty, "c5", hasMore: false));
            }

            if (request.Uri.Contains("/v1/backups/latest", StringComparison.Ordinal))
            {
                return Responses.Json(HttpStatusCode.OK, SnapshotJson.Manifest(body));
            }

            return request.Uri.Contains($"/v1/backups/{SnapshotJson.Id}", StringComparison.Ordinal)
                ? Responses.Bytes(HttpStatusCode.OK, body)
                : Responses.Error(HttpStatusCode.NotFound, "server.not_found");
        });

        await SeedCursorAsync(fixture.Database, "c-before");

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.True(report.Succeeded);
        Assert.True(report.Bootstrapped);
        Assert.Equal("Recovered", (await fixture.Database.Store.GetTaskAsync(Guid.Parse(Ids.Two)))?.Title);
        Assert.Equal("c5", (await fixture.Database.Store.GetStateAsync()).Cursor);
    }

    [Fact]
    public async Task Sync_invalid_without_the_snapshot_flag_keeps_local_data()
    {
        using var fixture = Create(_ =>
            Responses.Error(HttpStatusCode.BadRequest, WireErrorCodes.SyncInvalid));

        await fixture.Database.Store.ApplyPageAsync(new SyncPage(
            [
                new SyncedTask
                {
                    Id = Guid.Parse(Ids.One),
                    Title = "Cached",
                    CreatedAtUtc = Now.AddHours(-1),
                    UpdatedAtUtc = Now,
                    Revision = 1,
                    ServerRevision = 1,
                },
            ],
            [],
            "c-before",
            false,
            Now,
            null));

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(SyncErrorKind.Protocol, report.Error?.Kind);
        Assert.Equal(SyncStatusType.Failed, fixture.Engine.Status.Status);
        Assert.NotNull(await fixture.Database.Store.GetTaskAsync(Guid.Parse(Ids.One)));
        Assert.Equal("c-before", (await fixture.Database.Store.GetStateAsync()).Cursor);
        Assert.DoesNotContain(fixture.Handler.Requests, request =>
            request.Uri.Contains("/v1/backups/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_cursor_invalid_stops_without_looping()
    {
        var body = SnapshotJson.Body(
            SnapshotJson.Id,
            SnapshotJson.Cursor,
            40,
            (Ids.One, "From snapshot"));

        using var fixture = Create(request =>
        {
            if (request.Uri.Contains("/v1/sync/changes", StringComparison.Ordinal))
            {
                return Responses.Error(HttpStatusCode.Gone, WireErrorCodes.SyncCursorInvalid);
            }

            if (request.Uri.Contains("/v1/backups/latest", StringComparison.Ordinal))
            {
                return Responses.Json(HttpStatusCode.OK, SnapshotJson.Manifest(body));
            }

            return request.Uri.Contains($"/v1/backups/{SnapshotJson.Id}", StringComparison.Ordinal)
                ? Responses.Bytes(HttpStatusCode.OK, body)
                : Responses.Error(HttpStatusCode.NotFound, "server.not_found");
        });

        await SeedCursorAsync(fixture.Database, "c-before");

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(2, ChangesCalls(fixture).Count());
        Assert.Equal(SyncStatusType.NeedsRestore, fixture.Engine.Status.Status);
        Assert.Equal(
            ProjectionFreshness.NeedsRestore,
            (await fixture.Database.Store.GetStateAsync()).Freshness);
    }

    [Fact]
    public async Task A_corrupt_snapshot_needs_a_restore()
    {
        var body = SnapshotJson.Body(
            SnapshotJson.Id,
            SnapshotJson.Cursor,
            40,
            (Ids.One, "From snapshot"));

        using var fixture = Create(request =>
            request.Uri.Contains("/v1/backups/latest", StringComparison.Ordinal)
                ? Responses.Json(
                    HttpStatusCode.OK,
                    SnapshotJson.Manifest(
                        body,
                        contentSha256: new string('0', 64)))
                : Responses.Bytes(HttpStatusCode.OK, body));

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(SyncErrorKind.Corrupt, report.Error?.Kind);
        Assert.Equal(WireErrorCodes.BackupCorrupt, report.Error?.Code);
        Assert.Equal(SyncStatusType.NeedsRestore, fixture.Engine.Status.Status);
        Assert.Equal(
            ProjectionFreshness.NeedsRestore,
            (await fixture.Database.Store.GetStateAsync()).Freshness);
        Assert.Equal(0, await fixture.Database.Store.CountTasksAsync());
        Assert.Empty(ChangesCalls(fixture));
    }

    [Fact]
    public async Task An_oversized_snapshot_is_reported_without_downloading_it()
    {
        var body = SnapshotJson.Body(
            SnapshotJson.Id,
            SnapshotJson.Cursor,
            40,
            (Ids.One, "From snapshot"));

        using var fixture = Create(request =>
            request.Uri.Contains("/v1/backups/latest", StringComparison.Ordinal)
                ? Responses.Json(
                    HttpStatusCode.OK,
                    SnapshotJson.Manifest(
                        body,
                        sizeBytes: SnapshotBootstrapService.MaxSnapshotBytes + 1))
                : Responses.Bytes(HttpStatusCode.OK, body));

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(SyncErrorKind.SnapshotTooLarge, report.Error?.Kind);
        Assert.Equal(SyncStatusType.SnapshotTooLarge, fixture.Engine.Status.Status);
        Assert.Equal(
            ProjectionFreshness.SnapshotTooLarge,
            (await fixture.Database.Store.GetStateAsync()).Freshness);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task A_vanished_snapshot_is_reported()
    {
        var body = SnapshotJson.Body(
            SnapshotJson.Id,
            SnapshotJson.Cursor,
            40,
            (Ids.One, "From snapshot"));

        using var fixture = Create(request =>
            request.Uri.Contains("/v1/backups/latest", StringComparison.Ordinal)
                ? Responses.Json(HttpStatusCode.OK, SnapshotJson.Manifest(body))
                : Responses.Error(HttpStatusCode.NotFound, WireErrorCodes.BackupNotFound));

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(SyncErrorKind.SnapshotVanished, report.Error?.Kind);
        Assert.Equal(SyncStatusType.SnapshotVanished, fixture.Engine.Status.Status);
        Assert.Equal(
            ProjectionFreshness.NeedsRestore,
            (await fixture.Database.Store.GetStateAsync()).Freshness);
    }

    [Fact]
    public async Task Transport_failure_is_reported_as_offline_and_keeps_the_cursor()
    {
        using var fixture = Create(_ => throw new HttpRequestException("offline"));
        await SeedCursorAsync(fixture.Database, "c-before");

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(SyncErrorKind.Offline, report.Error?.Kind);
        Assert.Equal(SyncStatusType.Offline, fixture.Engine.Status.Status);
        Assert.Equal("c-before", (await fixture.Database.Store.GetStateAsync()).Cursor);
    }

    [Fact]
    public async Task Concurrent_calls_join_the_same_run()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = CreateAsync(async request =>
        {
            if (request.Uri.Contains("/v1/backups/latest", StringComparison.Ordinal))
            {
                return NoSnapshot();
            }

            entered.TrySetResult();
            await release.Task;
            return Responses.Changes(Page(string.Empty, "c1", hasMore: false));
        });

        var first = fixture.Engine.SyncOnceAsync();
        await entered.Task;
        var second = fixture.Engine.SyncOnceAsync();

        Assert.Same(first, second);
        release.SetResult();
        var report = await first;
        Assert.True(report.Succeeded);
        Assert.Single(ChangesCalls(fixture));
    }

    [Fact]
    public async Task Cancel_stops_the_run()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StubHandler handler = null!;
        Func<RecordedRequest, Task<HttpResponseMessage>> responder = async request =>
        {
            if (request.Uri.Contains("/v1/backups/latest", StringComparison.Ordinal))
            {
                return NoSnapshot();
            }

            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, handler.Token);
            return Responses.Changes(Page(string.Empty, "c1", hasMore: false));
        };
        handler = StubHandler.FromAsync(responder);
        using var fixture = Create(handler);

        var run = fixture.Engine.SyncOnceAsync();
        await entered.Task;
        fixture.Engine.Cancel();
        var report = await run;

        Assert.False(report.Succeeded);
        Assert.Equal(SyncErrorKind.Cancelled, report.Error?.Kind);
        Assert.Equal(SyncStatusType.Idle, fixture.Engine.Status.Status);
        Assert.Null((await fixture.Database.Store.GetStateAsync()).Cursor);
    }

    [Fact]
    public async Task Initialize_reflects_freshness_without_touching_the_network()
    {
        using var fixture = Create(_ =>
            throw new InvalidOperationException("InitializeAsync must not use the network"));

        await fixture.Engine.InitializeAsync();
        Assert.Equal(SyncStatusType.Idle, fixture.Engine.Status.Status);
        Assert.Null(fixture.Engine.Status.LastSyncedAt);

        await fixture.Database.Store.RecordAttemptAsync(ProjectionFreshness.NeedsRestore, Now);
        await fixture.Engine.InitializeAsync();
        Assert.Equal(SyncStatusType.NeedsRestore, fixture.Engine.Status.Status);

        await fixture.Database.Store.RecordAttemptAsync(ProjectionFreshness.SnapshotTooLarge, Now);
        await fixture.Engine.InitializeAsync();
        Assert.Equal(SyncStatusType.SnapshotTooLarge, fixture.Engine.Status.Status);

        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Initialize_reports_the_last_complete_sync()
    {
        using var fixture = Create(_ =>
            throw new InvalidOperationException("InitializeAsync must not use the network"));

        await SeedCursorAsync(fixture.Database, "c1");
        await fixture.Database.Store.CompleteSyncAsync(Now);

        await fixture.Engine.InitializeAsync();

        Assert.Equal(SyncStatusType.Idle, fixture.Engine.Status.Status);
        Assert.Equal(Now, fixture.Engine.Status.LastSyncedAt);
        Assert.Empty(fixture.Handler.Requests);
    }
}
