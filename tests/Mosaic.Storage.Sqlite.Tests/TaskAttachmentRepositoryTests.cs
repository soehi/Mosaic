using System.Globalization;
using Mosaic.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Storage.Sqlite;
using Mosaic.Tasks.Sqlite;

namespace Mosaic.Storage.Sqlite.Tests;

public sealed class TaskAttachmentRepositoryTests
{
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string id, Exception ex) { } }
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Expired_archived_query_filters_by_archive_cutoff_and_orders_attachments()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        try
        {
            await using var database = new SqliteModule(new(path), new Diagnostics());
            await database.ActivateAsync();
            await using var tasks = new TasksSqliteModule(database.Session);
            await tasks.ActivateAsync();
            var expiredTask = Guid.NewGuid();
            var freshTask = Guid.NewGuid();
            var activeTask = Guid.NewGuid();
            await InsertTask(database.Session, expiredTask, Now.AddDays(-8));
            await InsertTask(database.Session, freshTask, Now.AddDays(-6));
            await InsertTask(database.Session, activeTask, null);
            var later = Attachment(expiredTask, Now.AddDays(-3));
            var earlier = Attachment(expiredTask, Now.AddDays(-4));
            foreach (var attachment in new[] { later, earlier, Attachment(freshTask, Now.AddDays(-5)), Attachment(activeTask, Now.AddDays(-5)) })
                Assert.True((await tasks.AttachmentRepository.InsertAsync(attachment, StoredPath)).IsSuccess);

            var result = await tasks.AttachmentRepository.ListExpiredArchivedAsync(Now.AddDays(-7));

            Assert.True(result.IsSuccess);
            Assert.Equal([earlier.Id, later.Id], result.Value.Select(attachment => attachment.Id));
            var storedPath = await tasks.AttachmentRepository.FindStoredPathAsync(earlier.Id);
            Assert.True(storedPath.IsSuccess);
            Assert.Equal(StoredPath, storedPath.Value);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Batch_counts_group_by_task_and_skip_tasks_without_attachments()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        try
        {
            await using var database = new SqliteModule(new(path), new Diagnostics());
            await database.ActivateAsync();
            await using var tasks = new TasksSqliteModule(database.Session);
            await tasks.ActivateAsync();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var empty = Guid.NewGuid();
            foreach (var id in new[] { first, second, empty }) await InsertTask(database.Session, id, null);
            foreach (var attachment in new[] { Attachment(first, Now), Attachment(first, Now), Attachment(second, Now) })
                Assert.True((await tasks.AttachmentRepository.InsertAsync(attachment, StoredPath)).IsSuccess);

            var result = await tasks.AttachmentRepository.CountsByTasksAsync([first, second, empty, Guid.NewGuid()]);

            Assert.True(result.IsSuccess);
            Assert.Equal(2, result.Value[first]);
            Assert.Equal(1, result.Value[second]);
            Assert.False(result.Value.ContainsKey(empty));

            var none = await tasks.AttachmentRepository.CountsByTasksAsync([]);
            Assert.True(none.IsSuccess);
            Assert.Empty(none.Value);
        }
        finally { DeleteDatabase(path); }
    }
    private const string StoredPath = @"C:\attachments\attachment.txt";

    private static Mosaic.Tasks.Contracts.TaskAttachmentDto Attachment(Guid taskId, DateTimeOffset addedAt) =>
        new(Guid.NewGuid(), taskId, "attachment.txt", 10, addedAt);

    private static async Task InsertTask(IDatabaseSession database, Guid id, DateTimeOffset? archivedAt)
    {
        var result = await database.ExecuteAsync(new(
            "INSERT INTO tasks(id,title,notes,completed,created_at,updated_at,revision,archived_at) VALUES($id,'task','',1,$time,$time,1,$archived)",
            new Dictionary<string, object?>
            {
                ["$id"] = id.ToString("D"),
                ["$time"] = Now.ToString("O", CultureInfo.InvariantCulture),
                ["$archived"] = archivedAt?.ToString("O", CultureInfo.InvariantCulture)
            }));
        Assert.True(result.IsSuccess);
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }
}
