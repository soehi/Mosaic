using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Settings.Core;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Sqlite;
using Mosaic.Settings.Wpf;
using Mosaic.Storage.Sqlite;
using Mosaic.Tasks.Core;
using Mosaic.Tasks.Sqlite;
using Mosaic.Tasks.Wpf;

namespace Mosaic.Desktop;

internal sealed class ModuleRuntimeContext(
    AppPaths paths,
    StorageConfiguration storageConfiguration,
    TimeProvider clock,
    IIdGenerator ids,
    IDiagnosticSink diagnostics,
    IUiDispatcher dispatcher,
    IUserDialogService dialogs)
{
    internal AppPaths Paths { get; } = paths;
    internal StorageConfiguration StorageConfiguration { get; } = storageConfiguration;
    internal TimeProvider Clock { get; } = clock;
    internal IIdGenerator Ids { get; } = ids;
    internal IDiagnosticSink Diagnostics { get; } = diagnostics;
    internal IUiDispatcher Dispatcher { get; } = dispatcher;
    internal IUserDialogService Dialogs { get; } = dialogs;
    internal List<ISettingSource> DeferredSettingSources { get; } = [];
    internal SqliteModule? Storage { get; set; }
    internal TasksSqliteModule? Repository { get; set; }
    internal TasksModule? Tasks { get; set; }
    internal SettingsSqliteModule? SettingsRepository { get; set; }
    internal SettingsModule? Settings { get; set; }
    internal IHostSettingSource? HostSettings { get; init; }
}

internal static class BuiltInModuleCatalog
{
    private sealed record Entry(ModuleManifest Manifest, Func<ModuleRuntimeContext, IModuleActivation> Create);

    private static readonly IReadOnlyDictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal)
    {
        [SqliteModule.Definition.Id] = new(SqliteModule.Definition, context =>
        {
            context.Storage = new(new(
                context.Paths.Database(context.StorageConfiguration),
                StorageIntSetting(context.HostSettings, "storage.queueCapacity"),
                StorageIntSetting(context.HostSettings, "storage.busyTimeoutSeconds"),
                StorageIntSetting(context.HostSettings, "storage.backupRetention"),
                context.HostSettings?.GetValue("storage.journalMode") as string == "Wal" ? SqliteJournalMode.Wal : SqliteJournalMode.Delete,
                context.HostSettings?.GetValue("storage.connectionMode") as string == "PooledPerOperation" ? SqliteConnectionMode.PooledPerOperation : SqliteConnectionMode.ReusedSingle),
                context.Diagnostics);
            context.DeferredSettingSources.Add(context.Storage.Settings);
            return context.Storage;
        }),
        [TasksSqliteModule.Definition.Id] = new(TasksSqliteModule.Definition, context =>
        {
            context.Repository = new(context.Storage?.Session ?? throw new InvalidOperationException("Storage was not assembled before task repository."));
            return context.Repository;
        }),
        [TasksModule.Definition.Id] = new(TasksModule.Definition, context =>
        {
            context.Tasks = new(new(
                context.Repository?.Repository ?? throw new InvalidOperationException("Task repository was not assembled before task core."),
                context.Clock,
                context.Ids,
                new Mosaic.Kernel.ModuleExecutionGate(),
                context.Diagnostics,
                context.Settings?.Api));
            return context.Tasks;
        }),
        [TasksUiModule.Definition.Id] = new(TasksUiModule.Definition, context =>
        {
            return new TasksUiModule(new(
                context.Tasks?.Api ?? throw new InvalidOperationException("Task API was not assembled before task UI."),
                context.Dispatcher,
                context.Dialogs,
                context.Diagnostics,
                context.Settings?.Api));
        })
        ,
        [SettingsSqliteModule.Definition.Id] = new(SettingsSqliteModule.Definition, context =>
        {
            context.SettingsRepository = new(new(context.Storage?.Session ?? throw new InvalidOperationException("Storage was not assembled before settings repository.")));
            return context.SettingsRepository;
        }),
        [SettingsModule.Definition.Id] = new(SettingsModule.Definition, context =>
        {
            context.Settings = new(new(
                context.SettingsRepository?.Repository ?? throw new InvalidOperationException("Settings repository was not assembled before settings core."),
                context.HostSettings,
                new Mosaic.Kernel.ModuleExecutionGate(),
                context.Diagnostics,
                DeferredSources: context.DeferredSettingSources.ToArray()));
            return context.Settings;
        }),
        [SettingsUiModule.Definition.Id] = new(SettingsUiModule.Definition, context =>
        {
            return new SettingsUiModule(new(
                context.Settings?.Api ?? throw new InvalidOperationException("Settings API was not assembled before settings UI."),
                context.Dispatcher,
                context.Dialogs,
                context.Diagnostics));
        })
    };

    internal static IReadOnlyDictionary<string, ModuleManifest> Definitions { get; } =
        Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Manifest, StringComparer.Ordinal);

    internal static string[] DefaultModules { get; } =
        Entries.Values.Select(entry => entry.Manifest.Id).ToArray();

    internal static Dictionary<string, string> DefaultProviders { get; } =
        Entries.Values
            .SelectMany(entry => entry.Manifest.Provides.Select(capability => new KeyValuePair<string, string>(capability.ToString(), entry.Manifest.Id)))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    internal static IModuleActivation Create(ModuleRuntimeContext context, ModuleManifest manifest) =>
        Entries.TryGetValue(manifest.Id, out var entry)
            ? entry.Create(context)
            : throw new ConfigurationException("未知模块。");

    private static int StorageIntSetting(IHostSettingSource? hostSettings, string id)
    {
        var definition = new StorageSettingSource().Definitions.FirstOrDefault(item => item.Id == id)
            ?? throw new ConfigurationException("未知存储设置：" + id);
        var fallback = definition.DefaultValue is int value ? value : throw new ConfigurationException("存储设置默认值无效：" + id);
        var minimum = definition.Min is int min ? min : int.MinValue;
        var maximum = definition.Max is int max ? max : int.MaxValue;
        return hostSettings?.GetValue(id) is int configured && configured >= minimum && configured <= maximum
            ? configured
            : fallback;
    }
}
