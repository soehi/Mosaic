using Microsoft.Data.Sqlite;
using Mosaic.Mobile.Storage;

namespace Mosaic.Mobile.Sync.Tests;

public sealed class SqliteSyncStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static SyncedTask Task(
        string id,
        long serverRevision,
        string title = "Task",
        bool completed = false,
        DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.Parse(id),
        Title = title,
        Notes = "note",
        IsCompleted = completed,
        CreatedAtUtc = Now.AddHours(-1),
        UpdatedAtUtc = Now,
        Revision = serverRevision,
        CompletedAtUtc = completed ? Now : null,
        ArchivedAtUtc = archivedAt,
        ServerRevision = serverRevision,
        DeviceId = "device-1",
    };

    private static SyncPage Page(
        IEnumerable<SyncedTask>? tasks = null,
        IEnumerable<TaskTombstone>? tombstones = null,
        string cursor = "cursor-1",
        bool hasMore = false,
        string? expectedCursor = null) =>
        new(tasks?.ToArray() ?? [], tombstones?.ToArray() ?? [], cursor, hasMore, Now, expectedCursor);

    [Fact]
    public async Task Apply_page_writes_projection_and_cursor()
    {
        using var fixture = new TempDatabase();

        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 3)]));

        var state = await fixture.Store.GetStateAsync();
        var tasks = await fixture.Store.ListTasksAsync(MobileTaskFilter.All);

        Assert.Equal("cursor-1", state.Cursor);
        Assert.Equal(3, state.MaxServerRevision);
        Assert.Equal(ProjectionFreshness.Syncing, state.Freshness);
        Assert.Null(state.LastCompleteSyncAt);
        Assert.Equal(Now, state.LastSuccessfulPageAt);
        Assert.Single(tasks);
        Assert.Equal("Task", tasks[0].Title);
    }

    [Fact]
    public async Task Completing_a_run_marks_ready_and_records_completion()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 3)], hasMore: false));

        await fixture.Store.CompleteSyncAsync(Now.AddMinutes(1));

        var state = await fixture.Store.GetStateAsync();
        Assert.Equal(ProjectionFreshness.Ready, state.Freshness);
        Assert.Equal(Now.AddMinutes(1), state.LastCompleteSyncAt);
        Assert.Equal("cursor-1", state.Cursor);
    }

    [Fact]
    public async Task Complete_without_a_cursor_never_claims_ready()
    {
        using var fixture = new TempDatabase();

        await fixture.Store.CompleteSyncAsync(Now);

        var state = await fixture.Store.GetStateAsync();
        Assert.NotEqual(ProjectionFreshness.Ready, state.Freshness);
        Assert.Null(state.LastCompleteSyncAt);
    }

    [Fact]
    public async Task A_page_without_completion_never_marks_ready()
    {
        using var fixture = new TempDatabase();

        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 3)], hasMore: false));

        var state = await fixture.Store.GetStateAsync();
        Assert.Equal(ProjectionFreshness.Syncing, state.Freshness);
        Assert.Null(state.LastCompleteSyncAt);
        Assert.Null(state.LastCompleteSyncAt);
        Assert.Single(await fixture.Store.ListTasksAsync(MobileTaskFilter.All));
    }

    [Fact]
    public async Task A_repeated_next_cursor_is_rejected_without_side_effects()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 3)]));
        var stateBefore = await fixture.Store.GetStateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ApplyPageAsync(
            Page(tasks: [Task(Ids.One, 4)], cursor: "cursor-1")));

        Assert.Equal(stateBefore, await fixture.Store.GetStateAsync());
        Assert.Equal("3", (await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One)))?.ServerRevision.ToString());
    }

    [Fact]
    public async Task An_unexpected_cursor_leaves_the_projection_untouched()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 3)]));
        var stateBefore = await fixture.Store.GetStateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ApplyPageAsync(
            Page(tasks: [Task(Ids.Two, 4)], cursor: "cursor-2", expectedCursor: "stale-cursor")));

        Assert.Equal(stateBefore, await fixture.Store.GetStateAsync());
        Assert.Null(await fixture.Store.GetTaskAsync(Guid.Parse(Ids.Two)));
    }

    [Fact]
    public async Task Older_server_revision_never_overrides_newer()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 5, "New")]));

        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 2, "Old")], cursor: "cursor-2"));

        var task = await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One));
        Assert.Equal("New", task?.Title);
        Assert.Equal(5, task?.ServerRevision);
    }

    [Fact]
    public async Task Tombstone_hides_task_and_blocks_older_replay()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 1)]));
        await fixture.Store.ApplyPageAsync(Page(
            tombstones: [new TaskTombstone(Guid.Parse(Ids.One), 3, Now)], cursor: "cursor-2"));

        Assert.Equal(0, await fixture.Store.CountTasksAsync());

        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 2, "Resurrected")], cursor: "cursor-3"));
        Assert.Equal(0, await fixture.Store.CountTasksAsync());

        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 4, "Revived")], cursor: "cursor-4"));
        var task = await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One));
        Assert.Equal("Revived", task?.Title);
    }

    [Fact]
    public async Task Exact_revision_tombstone_wins_its_tie()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 1)]));
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 3, "Late")], cursor: "cursor-2"));

        await fixture.Store.ApplyPageAsync(Page(
            tombstones: [new TaskTombstone(Guid.Parse(Ids.One), 3, Now)], cursor: "cursor-3"));

        Assert.Equal(0, await fixture.Store.CountTasksAsync());
    }

    [Fact]
    public async Task Filters_separate_open_completed_and_archived()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks:
        [
            Task(Ids.One, 1, "Open"),
            Task(Ids.Two, 2, "Done", completed: true),
            Task(Ids.Three, 3, "Archived", completed: true, archivedAt: Now),
        ]));

        var open = await fixture.Store.ListTasksAsync(MobileTaskFilter.Open);
        var completed = await fixture.Store.ListTasksAsync(MobileTaskFilter.Completed);
        var archived = await fixture.Store.ListTasksAsync(MobileTaskFilter.Archived);
        var all = await fixture.Store.ListTasksAsync(MobileTaskFilter.All);

        Assert.Single(open);
        Assert.Single(completed);
        Assert.Equal("Done", completed[0].Title);
        Assert.Single(archived);
        Assert.Equal("Archived", archived[0].Title);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Replace_projection_keeps_a_baseline_not_a_completed_sync()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 1)]));

        await fixture.Store.ReplaceProjectionAsync(
            [Task(Ids.Two, 0, "Snapshot")],
            new ProjectionBaseline("snap-1", 12, Now, "cursor-snap"),
            Now);

        Assert.Equal("Snapshot", (await fixture.Store.GetTaskAsync(Guid.Parse(Ids.Two)))?.Title);
        Assert.Null(await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One)));
        var state = await fixture.Store.GetStateAsync();
        Assert.Equal("cursor-snap", state.Cursor);
        Assert.Equal("snap-1", state.BaselineSnapshotId);
        Assert.Equal(12, state.BaselineServerRevision);
        Assert.Equal(ProjectionFreshness.Bootstrapping, state.Freshness);
        Assert.Null(state.LastCompleteSyncAt);
        Assert.Null(state.LastSuccessfulPageAt);
        Assert.Equal(Now, state.LastAttemptAt);
    }

    [Fact]
    public async Task Snapshot_tasks_with_a_zero_revision_are_accepted()
    {
        using var fixture = new TempDatabase();

        await fixture.Store.ReplaceProjectionAsync(
            [Task(Ids.One, 0)],
            new ProjectionBaseline("snap-1", 12, Now, "cursor-snap"),
            Now);

        Assert.Equal(0, (await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One)))?.ServerRevision);
    }

    [Fact]
    public async Task Clear_all_resets_the_projection_but_keeps_the_store_usable()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 1)]));
        await fixture.Store.CompleteSyncAsync(Now);

        await fixture.Store.ClearAllAsync();

        var state = await fixture.Store.GetStateAsync();
        Assert.Equal(0, await fixture.Store.CountTasksAsync());
        Assert.Null(state.Cursor);
        Assert.Null(state.BaselineSnapshotId);
        Assert.Null(state.LastAttemptAt);
        Assert.Null(state.LastCompleteSyncAt);
        Assert.Null(state.LastSuccessfulPageAt);
        Assert.Equal(ProjectionFreshness.Unverified, state.Freshness);
        Assert.Equal(0, state.MaxServerRevision);

        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.Two, 2)]));
        Assert.Equal("cursor-1", (await fixture.Store.GetStateAsync()).Cursor);
        Assert.Equal(1, await fixture.Store.CountTasksAsync());
    }

    [Fact]
    public async Task Initialize_is_idempotent_for_a_validated_identity()
    {
        using var fixture = new TempDatabase();

        await fixture.Database.InitializeAsync(fixture.Store.Identity);
        await fixture.Database.InitializeAsync(fixture.Store.Identity);

        Assert.Equal(0, await fixture.Store.CountTasksAsync());
    }

    [Fact]
    public async Task Newer_schema_is_refused_and_preserved()
    {
        using var fixture = new TempDatabase();
        var dataSource = fixture.Database.DataSource;
        await using (var connection = new SqliteConnection($"Data Source={dataSource}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Database.InitializeAsync(fixture.Store.Identity));
    }
}
