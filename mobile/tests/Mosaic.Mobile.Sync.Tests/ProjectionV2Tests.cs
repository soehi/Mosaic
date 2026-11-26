using Microsoft.Data.Sqlite;
using Mosaic.Mobile.Storage;

namespace Mosaic.Mobile.Sync.Tests;

public sealed class ProjectionV2Tests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProjectionIdentity Owner = ProjectionIdentity.Create(
        "https://example.test", "user-1");

    private static SyncedTask Task(string id, long revision, string title = "Task") => new()
    {
        Id = Guid.Parse(id),
        Title = title,
        CreatedAtUtc = Now.AddHours(-1),
        UpdatedAtUtc = Now,
        Revision = revision,
        ServerRevision = revision,
    };

    [Fact]
    public async Task Older_tombstone_never_deletes_a_newer_task()
    {
        using var fixture = new ProjectionFixture();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 10, "New")]));
        await fixture.Store.ApplyPageAsync(Page(
            tombstones: [new TaskTombstone(Guid.Parse(Ids.One), 9, Now)],
            cursor: "cursor-2",
            expectedCursor: "cursor-1"));

        Assert.Equal("New", (await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One)))?.Title);
    }

    [Fact]
    public async Task Snapshot_baseline_requires_a_nonempty_cursor()
    {
        using var fixture = new ProjectionFixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.ReplaceProjectionAsync(
            [], new ProjectionBaseline("snap-1", 12, Now, " "), Now));
    }

    [Fact]
    public async Task Projections_are_isolated_by_server_and_user()
    {
        using var fixture = new ProjectionFixture();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 1, "Private")]));
        var other = await ProjectionStoreFactory.OpenOrCreateAsync(
            ProjectionIdentity.Create("https://example.test", "user-2"), fixture.Root);

        Assert.Empty(await other.ListTasksAsync(MobileTaskFilter.All));
        Assert.NotEqual(fixture.Store.Identity.OwnerKey, other.Identity.OwnerKey);
    }

    [Fact]
    public async Task Existing_v1_database_is_archived_then_v2_is_bootstrapped()
    {
        using var fixture = new ProjectionFixture(createStore: false);
        var legacyPath = Path.Combine(fixture.Root, "mosaic-mobile.db");
        await CreateLegacyV1Async(legacyPath);
        File.WriteAllText(legacyPath + "-wal", string.Empty);

        var result = await ProjectionMigration.MigrateLegacyV1Async(Owner, fixture.Root);

        Assert.Equal(ProjectionMigrationOutcome.Archived, result.Outcome);
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(Path.Combine(fixture.Root, result.ArchiveDirectory!, "mosaic-mobile.db")));
        Assert.True(File.Exists(Path.Combine(fixture.Root, result.ArchiveDirectory!, "mosaic-mobile.db-wal")));
        Assert.Equal(ProjectionMigrationOutcome.AlreadyArchived,
            (await ProjectionMigration.MigrateLegacyV1Async(Owner, fixture.Root)).Outcome);
    }

    [Fact]
    public async Task Invalid_v1_database_is_preserved_and_reported()
    {
        using var fixture = new ProjectionFixture(createStore: false);
        var legacyPath = Path.Combine(fixture.Root, "mosaic-mobile.db");
        await CreateLegacyV1Async(legacyPath);
        await using (var connection = new SqliteConnection($"Data Source={legacyPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 0;";
            await command.ExecuteNonQueryAsync();
        }

        var result = await ProjectionMigration.MigrateLegacyV1Async(Owner, fixture.Root);

        Assert.Equal(ProjectionMigrationOutcome.Rejected, result.Outcome);
        Assert.True(File.Exists(legacyPath));
        Assert.Contains("version", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Owner_mismatch_is_quarantined_instead_of_displayed()
    {
        using var fixture = new ProjectionFixture();
        var path = Path.Combine(fixture.Root, "projections", fixture.Store.Identity.OwnerKey + ".db");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE projection_meta SET owner_key = 'another-owner' WHERE singleton = 1;";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        await Assert.ThrowsAsync<ProjectionOwnershipException>(() =>
            ProjectionStoreFactory.OpenOrCreateAsync(Owner, fixture.Root));
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, "projections", "quarantine")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Same_page_upsert_ten_and_tombstone_nine_keeps_task_either_order(bool taskFirst)
    {
        using var fixture = new ProjectionFixture();
        var tombstone = new TaskTombstone(Guid.Parse(Ids.One), 9, Now);
        var tasks = new List<SyncedTask> { Task(Ids.One, 10, "New") };
        var tombstones = new List<TaskTombstone> { tombstone };

        await fixture.Store.ApplyPageAsync(new SyncPage(
            taskFirst ? tasks.ToArray() : Array.Empty<SyncedTask>(),
            taskFirst ? Array.Empty<TaskTombstone>() : tombstones.ToArray(),
            "cursor-1", false, Now, null));
        await fixture.Store.ApplyPageAsync(new SyncPage(
            taskFirst ? Array.Empty<SyncedTask>() : tasks.ToArray(),
            taskFirst ? tombstones.ToArray() : Array.Empty<TaskTombstone>(),
            "cursor-2", false, Now, "cursor-1"));

        Assert.Equal("New", (await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One)))?.Title);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Same_page_upsert_nine_and_tombstone_ten_deletes_task_either_order(bool taskFirst)
    {
        using var fixture = new ProjectionFixture();
        var tombstone = new TaskTombstone(Guid.Parse(Ids.One), 10, Now);
        var tasks = new List<SyncedTask> { Task(Ids.One, 9, "Old") };
        var tombstones = new List<TaskTombstone> { tombstone };

        await fixture.Store.ApplyPageAsync(new SyncPage(
            taskFirst ? tasks.ToArray() : Array.Empty<SyncedTask>(),
            taskFirst ? Array.Empty<TaskTombstone>() : tombstones.ToArray(),
            "cursor-1", false, Now, null));
        await fixture.Store.ApplyPageAsync(new SyncPage(
            taskFirst ? Array.Empty<SyncedTask>() : tasks.ToArray(),
            taskFirst ? tombstones.ToArray() : Array.Empty<TaskTombstone>(),
            "cursor-2", false, Now, "cursor-1"));

        Assert.Null(await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One)));
    }

    [Fact]
    public async Task Conflicting_task_payload_at_same_revision_fails_the_page()
    {
        using var fixture = new ProjectionFixture();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.ApplyPageAsync(Page(
            tasks: [Task(Ids.One, 5, "First"), Task(Ids.One, 5, "Second")])));

        Assert.Null(await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One)));
        Assert.Null((await fixture.Store.GetStateAsync()).Cursor);
    }

    [Fact]
    public async Task Incremental_task_with_zero_server_revision_fails()
    {
        using var fixture = new ProjectionFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 0)])));

        Assert.Null(await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One)));
    }

    [Fact]
    public async Task Highest_tombstone_revision_wins_for_the_same_id()
    {
        using var fixture = new ProjectionFixture();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 5, "Alive")]));

        await fixture.Store.ApplyPageAsync(Page(tombstones:
        [
            new TaskTombstone(Guid.Parse(Ids.One), 4, Now),
            new TaskTombstone(Guid.Parse(Ids.One), 6, Now),
        ], cursor: "cursor-2"));

        Assert.Null(await fixture.Store.GetTaskAsync(Guid.Parse(Ids.One)));
    }

    [Fact]
    public async Task Expected_cursor_mismatch_leaves_projection_untouched()
    {
        using var fixture = new ProjectionFixture();
        await fixture.Store.ApplyPageAsync(Page(tasks: [Task(Ids.One, 1)]));
        var stateBefore = await fixture.Store.GetStateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ApplyPageAsync(
            Page(tasks: [Task(Ids.Two, 2)], cursor: "cursor-2", expectedCursor: "wrong")));

        Assert.Equal(stateBefore, await fixture.Store.GetStateAsync());
        Assert.Null(await fixture.Store.GetTaskAsync(Guid.Parse(Ids.Two)));
    }

    [Fact]
    public async Task Nonempty_wal_is_archived_with_the_database()
    {
        using var fixture = new ProjectionFixture(createStore: false);
        var legacyPath = Path.Combine(fixture.Root, "mosaic-mobile.db");
        await CreateLegacyV1Async(legacyPath);
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = legacyPath,
                Pooling = false,
            }.ToString()))
        {
            await connection.OpenAsync();
            await using var wal = connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode = WAL; INSERT INTO meta(key, value) VALUES ('pending', 'write');";
            await wal.ExecuteNonQueryAsync();
        }

        var result = await ProjectionMigration.MigrateLegacyV1Async(Owner, fixture.Root);

        Assert.Equal(ProjectionMigrationOutcome.Archived, result.Outcome);
        Assert.False(File.Exists(legacyPath));
        Assert.False(File.Exists(legacyPath + "-wal"));
        var archived = Path.Combine(fixture.Root, result.ArchiveDirectory!, "mosaic-mobile.db");
        Assert.True(File.Exists(archived));
        Assert.True(new FileInfo(archived).Length > 0);
    }

    private static SyncPage Page(
        IEnumerable<SyncedTask>? tasks = null,
        IEnumerable<TaskTombstone>? tombstones = null,
        string cursor = "cursor-1",
        bool hasMore = false,
        string? expectedCursor = null) =>
        new(
            tasks?.ToArray() ?? [],
            tombstones?.ToArray() ?? [],
            cursor,
            hasMore,
            Now,
            expectedCursor);

    private static async Task CreateLegacyV1Async(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version = 1;
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE tasks (id TEXT PRIMARY KEY, title TEXT NOT NULL, notes TEXT NOT NULL,
                is_completed INTEGER NOT NULL, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL,
                revision INTEGER NOT NULL, completed_at_utc TEXT, archived_at_utc TEXT, restored_at_utc TEXT,
                server_revision INTEGER NOT NULL, device_id TEXT);
            CREATE TABLE task_tombstones (id TEXT PRIMARY KEY, server_revision INTEGER NOT NULL, deleted_at_utc TEXT);
            CREATE TABLE settings (key TEXT PRIMARY KEY, value_json TEXT NOT NULL, server_revision INTEGER NOT NULL, updated_at_utc TEXT);
            CREATE TABLE outbox (id TEXT PRIMARY KEY, kind TEXT NOT NULL, item_key TEXT NOT NULL,
                operation TEXT NOT NULL, payload_json TEXT, base_server_revision INTEGER NOT NULL,
                client_revision INTEGER, idempotency_key TEXT NOT NULL UNIQUE, state TEXT NOT NULL,
                created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
            INSERT INTO meta(key, value) VALUES ('sync.cursor', 'untrusted-cursor');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ProjectionFixture : IDisposable
    {
        public ProjectionFixture(bool createStore = true)
        {
            Root = Path.Combine(Path.GetTempPath(), "mosaic-v2-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            if (createStore)
            {
                _store = ProjectionStoreFactory.OpenOrCreateAsync(Owner, Root).GetAwaiter().GetResult();
            }
        }

        public string Root { get; }
        public SqliteSyncStore Store => _store
            ?? throw new InvalidOperationException("This fixture does not own a projection store.");
        private readonly SqliteSyncStore? _store;

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
