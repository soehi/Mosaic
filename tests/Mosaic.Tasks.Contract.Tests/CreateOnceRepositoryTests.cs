using System.Globalization;
using Mosaic.Contracts;
using Mosaic.Storage.Sqlite;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;
using Mosaic.Tasks.Sqlite;
using Mosaic.TestSupport;

namespace Mosaic.Tasks.Contract.Tests;

public sealed class CreateOnceRepositoryTests
{
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string id, Exception exception) { } }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_creation_has_one_receipt_and_survives_user_changes_and_deletion(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        await using var storage = new SqliteModule(new(path), new Diagnostics());
        TasksSqliteModule? adapter = null;
        ITaskRepository repository;
        if (sqlite)
        {
            await storage.ActivateAsync();
            adapter = new(storage.Session);
            await adapter.ActivateAsync();
            repository = adapter.Repository;
        }
        else repository = new MemoryTaskRepository();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var first = new TaskItemDto(Guid.NewGuid(), "更新简要", "complete contents", false, now, now, 1);
            var second = first with { Id = Guid.NewGuid() };
            // The duplicate title on an ordinary user task does not participate in idempotency.
            await repository.InsertAsync(first with { Id = Guid.NewGuid() });
            var outcomes = await Task.WhenAll(
                repository.InsertOnceAsync("release-summary", "stable-release", "fingerprint", first),
                repository.InsertOnceAsync("release-summary", "stable-release", "fingerprint", second));
            Assert.All(outcomes, result => Assert.True(result.IsSuccess));
            Assert.Single(outcomes, result => result.Value.Disposition == TaskCreateOnceDisposition.Created);
            Assert.Single(outcomes, result => result.Value.Disposition == TaskCreateOnceDisposition.AlreadyCreated);
            var id = outcomes[0].Value.TaskId;
            Assert.All(outcomes, result => Assert.Equal(id, result.Value.TaskId));
            var original = (await repository.FindAsync(id)).Value!;
            await repository.ReplaceAsync(original with { Title = "user edit", IsCompleted = true, Revision = 2 }, 1);
            var repeated = (await repository.InsertOnceAsync("release-summary", "stable-release", "fingerprint", first)).Value;
            Assert.Equal(TaskCreateOnceDisposition.AlreadyCreated, repeated.Disposition);
            Assert.True(repeated.TaskExists);
            Assert.Equal("user edit", (await repository.FindAsync(id)).Value!.Title);
            Assert.Equal(ErrorCodes.Conflict,
                (await repository.InsertOnceAsync("release-summary", "stable-release", "different payload", second)).Error?.Code);
            Assert.True((await repository.DeleteAsync(id, 2)).IsSuccess);
            repeated = (await repository.InsertOnceAsync("release-summary", "stable-release", "fingerprint", first)).Value;
            Assert.Equal(id, repeated.TaskId);
            Assert.False(repeated.TaskExists);
            var receipt = Assert.Single((await repository.QueryCreationReceiptsAsync("release-summary")).Value);
            Assert.Equal("stable-release", receipt.RequestKey);
            Assert.Equal("fingerprint", receipt.PayloadFingerprint);
            Assert.False(receipt.TaskExists);
            Assert.Empty((await repository.QueryCreationReceiptsAsync("another-source")).Value);
            Assert.Single((await repository.QueryAsync(new())).Value);
        }
        finally
        {
            if (adapter is not null) await adapter.DisposeAsync();
            await storage.StopAsync();
        }
        if (!sqlite) return;
        await using (var reopened = new SqliteModule(new(path), new Diagnostics()))
        {
            await reopened.ActivateAsync();
            await using var mapped = new TasksSqliteModule(reopened.Session);
            await mapped.ActivateAsync();
            var receipt = Assert.Single((await mapped.Repository.QueryCreationReceiptsAsync("release-summary")).Value);
            Assert.False(receipt.TaskExists);
            Assert.Equal(TaskCreateOnceDisposition.AlreadyCreated,
                (await mapped.Repository.InsertOnceAsync("release-summary", "stable-release", "fingerprint",
                    new(Guid.NewGuid(), "ignored", "", false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1))).Value.Disposition);
        }
        File.Delete(path);
    }

    [Fact]
    public async Task Receipt_write_failure_rolls_back_task_and_retry_commits_both()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        await using (var storage = new SqliteModule(new(path), new Diagnostics()))
        {
            await storage.ActivateAsync();
            await using var adapter = new TasksSqliteModule(storage.Session);
            await adapter.ActivateAsync();
            await storage.Session.ExecuteAsync(new(
                "CREATE TRIGGER fail_receipt BEFORE INSERT ON task_creation_receipts BEGIN SELECT RAISE(ABORT,'injected receipt failure'); END;"));
            var now = DateTimeOffset.UtcNow;
            var task = new TaskItemDto(Guid.NewGuid(), "更新简要", "full notes", false, now, now, 1);
            var failed = await adapter.Repository.InsertOnceAsync("source", "key", "fingerprint", task);
            Assert.Equal(ErrorCodes.StorageUnavailable, failed.Error?.Code);
            Assert.Empty((await adapter.Repository.QueryAsync(new())).Value);
            Assert.Empty((await adapter.Repository.QueryCreationReceiptsAsync("source")).Value);
            await storage.Session.ExecuteAsync(new("DROP TRIGGER fail_receipt"));
            var retried = await adapter.Repository.InsertOnceAsync("source", "key", "fingerprint", task);
            Assert.Equal(TaskCreateOnceDisposition.Created, retried.Value.Disposition);
            Assert.Single((await adapter.Repository.QueryAsync(new())).Value);
            Assert.Single((await adapter.Repository.QueryCreationReceiptsAsync("source")).Value);
        }
        File.Delete(path);
    }

    [Fact]
    public async Task Existing_task_database_migrates_with_backup_and_preserves_tasks()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        var assembly = typeof(TasksSqliteModule).Assembly;
        using var stream = assembly.GetManifestResourceStream("Mosaic.Tasks.Sqlite.Migrations.001_CreateTasks.sql")!;
        using var reader = new StreamReader(stream);
        var v1 = await reader.ReadToEndAsync();
        await using (var storage = new SqliteModule(new(path), new Diagnostics()))
        {
            await storage.ActivateAsync();
            Assert.True((await storage.Session.MigrateAsync("tasks-sqlite", [new(1, v1)])).IsSuccess);
            await storage.Session.ExecuteAsync(new(
                "INSERT INTO tasks(id,title,notes,completed,created_at,updated_at,revision) VALUES('00000000-0000-0000-0000-000000000001','existing','',0,'2026-11-16T00:00:00.0000000+00:00','2026-11-16T00:00:00.0000000+00:00',1)"));
            using var receiptStream = assembly.GetManifestResourceStream("Mosaic.Tasks.Sqlite.Migrations.002_CreateTaskReceipts.sql")!;
            using var receiptReader = new StreamReader(receiptStream);
            var v2 = await receiptReader.ReadToEndAsync();
            var failedMigration = await storage.Session.MigrateAsync("tasks-sqlite", [new(1, v1), new(2, v2 + "\nINVALID SQL;")]);
            Assert.False(failedMigration.IsSuccess);
            Assert.Empty((await storage.Session.QueryAsync(new("SELECT name FROM sqlite_master WHERE name='task_creation_receipts'"))).Value);
            Assert.Equal(1, Assert.Single((await storage.Session.QueryAsync(new("SELECT version FROM mosaic_migrations WHERE module_id='tasks-sqlite'"))).Value).Int64("version"));
            await using var adapter = new TasksSqliteModule(storage.Session);
            await adapter.ActivateAsync();
            Assert.Equal("existing", Assert.Single((await adapter.Repository.QueryAsync(new())).Value).Title);
            Assert.Empty((await adapter.Repository.QueryCreationReceiptsAsync("source")).Value);
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".backup-*"));
        }
        File.Delete(path);
        foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".backup-*"))
            File.Delete(backup);
    }

    [Fact]
    public async Task Old_database_backfills_completed_time_and_stays_unarchived()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        var assembly = typeof(TasksSqliteModule).Assembly;
        var migrations = await Task.WhenAll(new[] { "001_CreateTasks", "002_CreateTaskReceipts", "003_TaskArchiving" }
            .Select(async name =>
            {
                using var stream = assembly.GetManifestResourceStream("Mosaic.Tasks.Sqlite.Migrations." + name + ".sql")!;
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }));
        await using (var storage = new SqliteModule(new(path), new Diagnostics()))
        {
            await storage.ActivateAsync();
            Assert.True((await storage.Session.MigrateAsync("tasks-sqlite", [new(1, migrations[0])])).IsSuccess);
            await storage.Session.ExecuteAsync(new(
                """
                INSERT INTO tasks(id,title,notes,completed,created_at,updated_at,revision) VALUES
                ('00000000-0000-0000-0000-000000000001','done','',1,'2026-11-01T00:00:00.0000000+00:00','2026-11-08T12:00:00.0000000+00:00',3),
                ('00000000-0000-0000-0000-000000000002','open','',0,'2026-11-01T00:00:00.0000000+00:00','2026-11-09T12:00:00.0000000+00:00',1)
                """));
            Assert.True((await storage.Session.MigrateAsync("tasks-sqlite",
                [new(1, migrations[0]), new(2, migrations[1]), new(3, migrations[2])])).IsSuccess);
            await using var adapter = new TasksSqliteModule(storage.Session);
            await adapter.ActivateAsync();
            var active = await adapter.Repository.QueryAsync(new());
            Assert.Equal(2, active.Value.Count);
            Assert.DoesNotContain(active.Value, task => task.IsArchived);
            var archived = await adapter.Repository.QueryAsync(new() { ArchiveScope = Mosaic.Tasks.Contracts.TaskArchiveScope.Archived });
            Assert.Empty(archived.Value);
            var all = (await adapter.Repository.QueryAsync(new(Mosaic.Tasks.Contracts.TaskFilter.All) { ArchiveScope = Mosaic.Tasks.Contracts.TaskArchiveScope.All })).Value;
            var done = Assert.Single(all, task => task.Title == "done");
            Assert.False(done.IsArchived);
            Assert.Equal(DateTimeOffset.ParseExact("2026-11-08T12:00:00.0000000+00:00", "O", CultureInfo.InvariantCulture), done.CompletedAtUtc);
            Assert.Null(Assert.Single(all, task => task.Title == "open").CompletedAtUtc);
            var candidates = await adapter.Repository.QueryArchiveCandidatesAsync(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), 10);
            Assert.Equal(done.Id, Assert.Single(candidates.Value).Id);
        }
        File.Delete(path);
        foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".backup-*"))
            File.Delete(backup);
    }
}
