using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Storage.Sqlite.Internal;

namespace Mosaic.Storage.Sqlite;

public enum SqliteJournalMode { Delete, Wal }
public enum SqliteConnectionMode { ReusedSingle, PooledPerOperation }

public sealed record SqliteOptions(
    string DatabasePath,
    int QueueCapacity = 128,
    int BusyTimeoutSeconds = 3,
    int BackupRetention = 5,
    SqliteJournalMode JournalMode = SqliteJournalMode.Delete,
    SqliteConnectionMode ConnectionMode = SqliteConnectionMode.ReusedSingle);

public sealed class StorageSettingSource : ISettingSource
{
    public string ModuleId => "storage-sqlite";

    public IReadOnlyList<SettingDefinition> Definitions { get; } = Localize(
    [
        new("storage.queueCapacity", "storage-sqlite", "存储队列容量", "数据库操作队列的最大排队数量", SettingValueKind.Integer, 128, 16, 4096, null, SettingsGroup.StorageSystem, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
        new("storage.busyTimeoutSeconds", "storage-sqlite", "存储忙等超时", "数据库忙时的等待秒数", SettingValueKind.Integer, 3, 1, 30, null, SettingsGroup.StorageSystem, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
        new("storage.backupRetention", "storage-sqlite", "备份保留数量", "迁移前自动备份的保留份数，0 表示不保留", SettingValueKind.Integer, 5, 0, 50, null, SettingsGroup.StorageSystem, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
        new("storage.journalMode", "storage-sqlite", "日志模式", "SQLite 日志模式；WAL 会产生 -wal/-shm 文件", SettingValueKind.Enum, "Delete", null, null, ["Delete", "Wal"], SettingsGroup.StorageSystem, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig, "切换前会 checkpoint，备份仍保持一致快照"),
        new("storage.connectionMode", "storage-sqlite", "连接模式", "复用单连接或每次操作独立连接；均由单消费者队列串行执行", SettingValueKind.Enum, "ReusedSingle", null, null, ["ReusedSingle", "PooledPerOperation"], SettingsGroup.StorageSystem, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig)
    ]);

    private static IReadOnlyList<SettingDefinition> Localize(IEnumerable<SettingDefinition> definitions) =>
        definitions.Select(definition => definition with
        {
            DisplayNameKey = DisplayNames[definition.Id],
            DescriptionKey = DisplayNames[definition.Id] + ".Description",
            Options = definition.AllowedValues is { Count: > 0 } values
                ? values.Select(value => new SettingOption(value, "Setting.Value." + value)).ToArray()
                : null
        }).ToArray();

    private static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["storage.queueCapacity"] = "Settings.Setting.QueueCapacity",
        ["storage.busyTimeoutSeconds"] = "Settings.Setting.BusyTimeout",
        ["storage.backupRetention"] = "Settings.Setting.BackupRetention",
        ["storage.journalMode"] = "Settings.Setting.JournalMode",
        ["storage.connectionMode"] = "Settings.Setting.ConnectionMode"
    };

    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class SqliteModule : IModuleActivation
{
    private readonly SqliteOptions _options;
    private readonly IDiagnosticSink _diagnostics;
    private DatabaseWorker? _worker;
    public ISettingSource Settings { get; } = new StorageSettingSource();
    public SqliteModule(SqliteOptions options, IDiagnosticSink diagnostics) { _options = options; _diagnostics = diagnostics; }
    public static ModuleManifest Definition { get; } = new(
        "storage-sqlite",
        [new("sqlite.session", 1)],
        [],
        "Owns the SQLite connection, serialized execution queue, migrations and backups.");
    public ModuleManifest Manifest => Definition;
    public IDatabaseSession Session => _worker ?? throw new InvalidOperationException("Storage is not active.");
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_worker is not null) throw new InvalidOperationException("Storage already started.");
        _worker = new DatabaseWorker(_options, _diagnostics);
        await _worker.StartAsync(cancellationToken).ConfigureAwait(false);
    }
    public Task StopAsync() => _worker?.StopAsync() ?? Task.CompletedTask;
    public async ValueTask DisposeAsync() { if (_worker is not null) await _worker.DisposeAsync().ConfigureAwait(false); }
}
