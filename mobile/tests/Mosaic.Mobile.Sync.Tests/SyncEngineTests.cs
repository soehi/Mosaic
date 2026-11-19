using System.Net;
using System.Text.Json;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync.Tests;

public sealed class SyncEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static string Page(
        IEnumerable<ChangeDto> changes,
        string? nextCursor,
        bool hasMore) =>
        WireJson.Serialize(new SyncChangesResponseDto
        {
            Changes = [.. changes],
            NextCursor = nextCursor,
            HasMore = hasMore,
        });

    private static ChangeDto TaskChange(string id, long serverRevision, string title) => new()
    {
        ServerRevision = serverRevision,
        Kind = "task",
        Key = id,
        Operation = "upsert",
        Payload = JsonSerializer.SerializeToElement(
            new TaskItemDto
            {
                Id = Guid.Parse(id),
                Title = title,
                CreatedAtUtc = Now.AddHours(-1),
                UpdatedAtUtc = Now,
                Revision = serverRevision,
            },
            WireJson.Options),
        UpdatedAtUtc = Now,
    };

    private static ChangeDto SettingChange(string key, string value, long serverRevision) => new()
    {
        ServerRevision = serverRevision,
        Kind = "setting",
        Key = key,
        Operation = "upsert",
        Payload = JsonSerializer.SerializeToElement(new { value }),
        UpdatedAtUtc = Now,
    };

    private sealed record Fixture(
        SyncEngine Engine,
        TempDatabase Database,
        PortableSettings Settings,
        StubHandler Handler) : IDisposable
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
        var settings = new PortableSettings();
        var backups = new BackupRestoreService(api, settings);
        var engine = new SyncEngine(api, database.Store, backups, settings);
        return new Fixture(engine, database, settings, handler);
    }

    [Fact]
    public async Task Two_pages_are_applied_and_the_cursor_tracks_the_last_page()
    {
        using var fixture = Create(request =>
        {
            var uri = request.Uri;
            if (uri.Contains("cursor=c1", StringComparison.Ordinal))
            {
                return Responses.Changes(Page(
                    [TaskChange(Ids.Two, 2, "Second")],
                    "c2",
                    hasMore: false));
            }

            return Responses.Changes(Page(
                [
                    TaskChange(Ids.One, 1, "First"),
                    SettingChange("ui.language", "English", 2),
                ],
                "c1",
                hasMore: true));
        });

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.Pages);
        Assert.Equal(3, report.Changes);
        Assert.Equal(2, await fixture.Database.Store.CountTasksAsync());
        var state = await fixture.Database.Store.GetStateAsync();
        Assert.Equal("c2", state.Cursor);
        Assert.Equal(AppLanguagePreference.English, fixture.Settings.Language);
        Assert.Equal(SyncStatusType.Succeeded, fixture.Engine.Status.Status);
        Assert.NotNull(fixture.Engine.Status.LastSyncedAt);
    }

    [Fact]
    public async Task A_run_stops_after_twenty_pages()
    {
        var page = 0;
        using var fixture = Create(_ => Responses.Changes(Page(
            [TaskChange(Ids.One, 1, "Task")],
            $"c{page}",
            hasMore: true)));

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(SyncEngine.MaxPagesPerRun, report.Pages);
        Assert.Equal(SyncEngine.MaxPagesPerRun, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task Unauthorized_marks_needs_sign_in()
    {
        using var fixture = Create(_ => Responses.Error(HttpStatusCode.Unauthorized, "auth.session_expired"));

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(SyncErrorKind.SessionExpired, report.Error?.Kind);
        Assert.Equal(SyncStatusType.NeedsSignIn, fixture.Engine.Status.Status);
    }

    [Fact]
    public async Task Cursor_invalid_restores_the_snapshot_then_continues()
    {
        var changesCalls = 0;
        var snapshotItem = new
        {
            kind = "task",
            key = Ids.One,
            revision = 4,
            payload = new TaskItemDto
            {
                Id = Guid.Parse(Ids.One),
                Title = "From snapshot",
                CreatedAtUtc = Now.AddHours(-1),
                UpdatedAtUtc = Now,
                Revision = 4,
            },
        };
        var snapshot = new
        {
            snapshotId = "snap-1",
            createdAt = Now,
            sizeBytes = 100,
            itemCount = 1,
            items = new[] { snapshotItem },
        };

        using var fixture = Create(request =>
        {
            var uri = request.Uri;
            if (uri.Contains("/v1/sync/changes", StringComparison.Ordinal))
            {
                changesCalls++;
                return changesCalls == 1
                    ? Responses.Json(
                        (HttpStatusCode)410,
                        """{"code":"sync.cursor_invalid","details":{"requiresSnapshot":true}}""")
                    : Responses.Changes(Page([], "c9", hasMore: false));
            }

            if (uri.Contains("/v1/backups/latest", StringComparison.Ordinal)
                || uri.Contains("/v1/backups/snap-1", StringComparison.Ordinal))
            {
                return Responses.Json(HttpStatusCode.OK, WireJson.Serialize(snapshot));
            }

            return Responses.Error(HttpStatusCode.NotFound, "server.not_found");
        });

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.True(report.Succeeded);
        Assert.True(report.RebuiltFromSnapshot);
        Assert.Equal("From snapshot", (await fixture.Database.Store.GetTaskAsync(Guid.Parse(Ids.One)))?.Title);
        Assert.Equal("c9", (await fixture.Database.Store.GetStateAsync()).Cursor);
        Assert.Equal(2, changesCalls);
    }

    [Fact]
    public async Task A_second_cursor_invalid_stops_without_looping()
    {
        using var fixture = Create(request =>
            request.Uri.Contains("/v1/sync/changes", StringComparison.Ordinal)
                ? Responses.Json(
                    (HttpStatusCode)410,
                    """{"code":"sync.cursor_invalid","details":{"requiresSnapshot":true}}""")
                : Responses.Error(HttpStatusCode.NotFound, "backup.not_found"));

        var report = await fixture.Engine.SyncOnceAsync();

        Assert.False(report.Succeeded);
        Assert.True(report.NeedsSnapshot);
        Assert.Equal(SyncStatusType.RebuildRequired, fixture.Engine.Status.Status);
        Assert.Equal(2, fixture.Handler.Requests.Count(request => request.Uri.Contains("/sync/changes", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Transport_failure_is_reported_as_offline_and_keeps_the_cursor()
    {
        using var fixture = Create(_ => throw new HttpRequestException("offline"));
        await fixture.Database.Store.ApplyPageAsync(new SyncPage(
            [],
            [],
            [],
            "c-before",
            Now));

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
        using var fixture = CreateAsync(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return Responses.Changes(Page([], null, hasMore: false));
        });

        var first = fixture.Engine.SyncOnceAsync();
        await entered.Task;
        var second = fixture.Engine.SyncOnceAsync();

        Assert.Same(first, second);
        release.SetResult();
        var report = await first;
        Assert.True(report.Succeeded);
    }

    [Fact]
    public async Task Cancel_stops_the_run()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StubHandler handler = null!;
        Func<RecordedRequest, Task<HttpResponseMessage>> responder = async _ =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, handler.Token);
            return Responses.Changes(Page([], null, hasMore: false));
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
    }

    [Fact]
    public async Task Initialize_applies_cached_settings_offline()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(new SyncPage(
            [],
            [],
            [
                new StoredSetting("ui.language", "\"English\"", 1, Now),
                new StoredSetting("appearance.theme", "\"Dark\"", 2, Now),
            ],
            "c1",
            Now));

        var handler = new StubHandler(_ => throw new InvalidOperationException("no network expected"));
        var api = new MosaicApiClient(new HttpClient(handler)) { BaseUrl = "https://example.test" };
        var settings = new PortableSettings();
        var engine = new SyncEngine(api, fixture.Store, new BackupRestoreService(api, settings), settings);

        await engine.InitializeAsync();

        Assert.Equal(AppLanguagePreference.English, settings.Language);
        Assert.Equal(ThemePreference.Dark, settings.Theme);
        Assert.Equal(SyncStatusType.Idle, engine.Status.Status);
        Assert.NotNull(engine.Status.LastSyncedAt);
    }
}
