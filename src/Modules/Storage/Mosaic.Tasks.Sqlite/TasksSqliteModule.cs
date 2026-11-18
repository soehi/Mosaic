using Mosaic.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Tasks.Persistence.Contracts;
using Mosaic.Tasks.Sqlite.Internal;

namespace Mosaic.Tasks.Sqlite;

public sealed class TasksSqliteModule(IDatabaseSession database) : IModuleActivation
{
    private bool _active;
    private readonly ITaskRepository _repository = new SqliteTaskRepository(database);
    public static ModuleManifest Definition { get; } = new(
        "tasks-sqlite",
        [new("tasks.repository", 1)],
        [new("sqlite.session", 1)],
        "Owns task persistence mapping, SQL and database migrations.");
    public ModuleManifest Manifest => Definition;
    public ITaskRepository Repository => _active ? _repository : throw new InvalidOperationException("Task repository is not active.");
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        var migrations = new List<MigrationDefinition>();
        foreach (var (version, name) in new[] { (1, "001_CreateTasks"), (2, "002_CreateTaskReceipts"), (3, "003_TaskArchiving") })
        {
            using var stream = typeof(TasksSqliteModule).Assembly.GetManifestResourceStream($"Mosaic.Tasks.Sqlite.Migrations.{name}.sql")
                ?? throw new InvalidOperationException("Missing embedded migration.");
            using var reader = new StreamReader(stream);
            migrations.Add(new(version, await reader.ReadToEndAsync(cancellationToken)));
        }
        var result = await database.MigrateAsync("tasks-sqlite", migrations, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) throw new InvalidOperationException($"任务数据迁移失败：{result.Error!.Code} ({result.Error.CorrelationId})");
        _active = true;
    }
    public Task StopAsync() { _active = false; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { _active = false; return ValueTask.CompletedTask; }
}
