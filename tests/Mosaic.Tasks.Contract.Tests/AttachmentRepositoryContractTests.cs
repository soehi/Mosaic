using Mosaic.Contracts;
using Mosaic.Storage.Sqlite;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;
using Mosaic.Tasks.Sqlite;

namespace Mosaic.Tasks.Contract.Tests;

public sealed class AttachmentRepositoryContractTests
{
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string id, Exception ex) { } }
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Stored_path_is_persisted_through_the_repository_port_not_the_public_dto()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var storage = new SqliteModule(new(path), new Diagnostics());
            await storage.ActivateAsync();
            await using var tasks = new TasksSqliteModule(storage.Session);
            await tasks.ActivateAsync();
            var repository = tasks.AttachmentRepository;
            var taskId = Guid.NewGuid();
            var insert = await storage.Session.ExecuteAsync(new(
                "INSERT INTO tasks(id,title,notes,completed,created_at,updated_at,revision) VALUES($id,'t','',0,$t,$t,1)",
                new Dictionary<string, object?> { ["$id"] = taskId.ToString("D"), ["$t"] = Now.ToString("O") }));
            Assert.True(insert.IsSuccess);

            var attachment = new TaskAttachmentDto(Guid.NewGuid(), taskId, "a.txt", 3, Now);
            const string storedPath = @"C:\store\task\attachment.txt";
            Assert.True((await repository.InsertAsync(attachment, storedPath)).IsSuccess);

            Assert.Equal(storedPath, (await repository.FindStoredPathAsync(attachment.Id)).Value);
            Assert.Equal(attachment, (await repository.FindAsync(attachment.Id)).Value);
            Assert.Equal([attachment], (await repository.ListByTaskAsync(taskId)).Value);
            Assert.Null((await repository.FindStoredPathAsync(Guid.NewGuid())).Value);

            Assert.True((await repository.DeleteAsync(attachment.Id)).IsSuccess);
            Assert.Null((await repository.FindStoredPathAsync(attachment.Id)).Value);
        }
        finally
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(candidate)) File.Delete(candidate);
        }
    }
}