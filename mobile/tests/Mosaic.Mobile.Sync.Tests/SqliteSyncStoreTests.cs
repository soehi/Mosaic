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
        IEnumerable<StoredSetting>? settings = null,
        string? cursor = "cursor-1") =>
        new(tasks?.ToArray() ?? [], tombstones?.ToArray() ?? [], settings?.ToArray() ?? [], cursor, Now);

    [Fact]
    public async Task Apply_page_writes_projection_and_cursor()
    {
        using var fixture = new TempDatabase();

        await fixture.Store.ApplyPageAsync(Page(
            tasks: [Task(Ids.One, 3)],
            settings: [new StoredSetting("ui.language", "\"English\"", 4, Now)]));

        var state = await fixture.Store.GetStateAsync();
        var tasks = await fixture.Store.ListTasksAsync(MobileTaskFilter.All);
        var settings = await fixture.Store.ListSettingsAsync();

        Assert.Equal("cursor-1", state.Cursor);
        Assert.Equal(4, state.MaxServerRevision);
        Assert.NotNull(state.LastSyncedAt);
        Assert.Single(tasks);
        Assert.Equal("Task", tasks[0].Title);
        Assert.Single(settings);
        Assert.Equal("\"English\"", settings[0].ValueJson);
    }

    [Fact]
    public async Task Replaying_the_same_page_is_idempotent()
    {
        using var fixture = new TempDatabase();
        var page = Page(tasks: [Task(Ids.One, 3)]);

        await fixture.Store.ApplyPageAsync(page);
        await fixture.Store.ApplyPageAsync(page);

        Assert.Equal(1, await fixture.Store.CountTasksAsync());
    }

    [Fact]
    public async Task Older_server_revision_never_overrides_newer()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 5, "New")]));

        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 2, "Old")]));

        var task = await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One));
        Assert.Equal("New", task?.Title);
    }

    [Fact]
    public async Task Tombstone_hides_task_and_blocks_older_replay()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 1)]));
        await fixture.Store.ApplyPageAsync(Page(
            tombstones: [new TaskTombstone(Guid.Parse(Ids.One), 3, Now)]));

        Assert.Equal(0, await fixture.Store.CountTasksAsync());

        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 2, "Resurrected")]));
        Assert.Equal(0, await fixture.Store.CountTasksAsync());

        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 4, "Revived")]));
        var task = await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One));
        Assert.Equal("Revived", task?.Title);
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
    public async Task Replace_projection_discards_previous_rows()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 1)]));

        await fixture.Store.ReplaceProjectionAsync(
            [Task(Ids.Two, 0, "Snapshot")],
            [new StoredSetting("appearance.theme", "\"Dark\"", 0, Now)],
            cursor: null);

        Assert.Equal("Snapshot", (await fixture.Store.GetTaskAsync(Guid.Parse(Ids.Two)))?.Title);
        Assert.Null(await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One)));
        var state = await fixture.Store.GetStateAsync();
        Assert.Null(state.Cursor);
        Assert.Single(await fixture.Store.ListSettingsAsync());
    }

    [Fact]
    public async Task Reset_projection_keeps_last_synced_but_clears_cursor()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 1)]));

        await fixture.Store.ResetProjectionAsync();

        var state = await fixture.Store.GetStateAsync();
        Assert.Equal(0, await fixture.Store.CountTasksAsync());
        Assert.Null(state.Cursor);
        Assert.NotNull(state.LastSyncedAt);
    }

    [Fact]
    public async Task Clear_all_removes_everything()
    {
        using var fixture = new TempDatabase();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 1)]));

        await fixture.Store.ClearAllAsync();

        var state = await fixture.Store.GetStateAsync();
        Assert.Equal(0, await fixture.Store.CountTasksAsync());
        Assert.Empty(await fixture.Store.ListSettingsAsync());
        Assert.Null(state.Cursor);
        Assert.Null(state.LastSyncedAt);
    }

    [Fact]
    public async Task Initialize_is_idempotent()
    {
        using var fixture = new TempDatabase();

        await fixture.Database.InitializeAsync();
        await fixture.Database.InitializeAsync();

        Assert.Equal(0, await fixture.Store.CountTasksAsync());
    }

    [Fact]
    public async Task Newer_schema_is_refused()
    {
        using var fixture = new TempDatabase();
        await using (var connection = new SqliteConnection($"Data Source={fixture.Database.DataSource}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Database.InitializeAsync());
    }
}
