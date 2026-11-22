using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Mosaic.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Storage.Sqlite;
using Mosaic.Tasks.Sqlite;

namespace Mosaic.Storage.Sqlite.Tests;

public sealed class TaskAttachmentMigrationTests
{
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string id, Exception ex) { } }
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Legacy_004_database_with_orphan_rows_upgrades_to_005_with_foreign_key_and_cascade()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            // 构造 0.18.x 老库：001-004 已应用，task_attachments 无 FK，含一行孤儿附件。
            var taskId = Guid.NewGuid();
            var keptAttachment = Guid.NewGuid();
            var orphanAttachment = Guid.NewGuid();
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await Execute(connection, "CREATE TABLE IF NOT EXISTS mosaic_migrations(module_id TEXT NOT NULL, version INTEGER NOT NULL, digest TEXT NOT NULL, PRIMARY KEY(module_id, version));");
                foreach (var (version, name) in new[] { (1, "001_CreateTasks"), (2, "002_CreateTaskReceipts"), (3, "003_TaskArchiving"), (4, "004_TaskAttachments") })
                {
                    using var stream = typeof(TasksSqliteModule).Assembly.GetManifestResourceStream($"Mosaic.Tasks.Sqlite.Migrations.{name}.sql")!;
                    using var reader = new StreamReader(stream);
                    var sql = await reader.ReadToEndAsync();
                    await Execute(connection, sql);
                    await Execute(connection,
                        "INSERT INTO mosaic_migrations(module_id,version,digest) VALUES('tasks-sqlite',$v,$d);",
                        ("$v", version), ("$d", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)))));
                }
                var stamp = Now.ToString("O", CultureInfo.InvariantCulture);
                await Execute(connection,
                    "INSERT INTO tasks(id,title,notes,completed,created_at,updated_at,revision) VALUES($id,'task','',1,$t,$t,1);",
                    ("$id", taskId.ToString("D")), ("$t", stamp));
                foreach (var (id, owner) in new[] { (keptAttachment, taskId), (orphanAttachment, Guid.NewGuid()) })
                    await Execute(connection,
                        "INSERT INTO task_attachments(id,task_id,file_name,stored_path,size_bytes,added_at) VALUES($id,$task,'a.txt','C:\\a.txt',1,$t);",
                        ("$id", id.ToString("D")), ("$task", owner.ToString("D")), ("$t", stamp));
            }

            await using var database = new SqliteModule(new(path), new Diagnostics());
            await database.ActivateAsync();
            await using var tasks = new TasksSqliteModule(database.Session);
            await tasks.ActivateAsync();

            // 孤儿行被清除，正常行保留。
            var remaining = await tasks.AttachmentRepository.ListByTaskAsync(taskId);
            Assert.True(remaining.IsSuccess);
            Assert.Equal([keptAttachment], remaining.Value.Select(a => a.Id));
            var orphan = await tasks.AttachmentRepository.FindAsync(orphanAttachment);
            Assert.True(orphan.IsSuccess);
            Assert.Null(orphan.Value);

            // FK 生效：插入指向不存在任务的附件被拒绝。
            var dangling = await tasks.AttachmentRepository.InsertAsync(
                new(Guid.NewGuid(), Guid.NewGuid(), "x.txt", 1, Now), @"C:\x.txt");
            Assert.False(dangling.IsSuccess);

            // ON DELETE CASCADE：删任务后附件行级联消失。
            var delete = await database.Session.ExecuteAsync(new(
                "DELETE FROM tasks WHERE id = $id",
                new Dictionary<string, object?> { ["$id"] = taskId.ToString("D") }));
            Assert.True(delete.IsSuccess);
            var cascaded = await tasks.AttachmentRepository.FindAsync(keptAttachment);
            Assert.True(cascaded.IsSuccess);
            Assert.Null(cascaded.Value);
        }
        finally { DeleteDatabase(path); }
    }

    private static async Task Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
        foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".backup-*.sqlite"))
            File.Delete(backup);
    }
}
