using Mosaic.Auth.Core;
using Mosaic.Auth.Http;
using Mosaic.Auth.Windows;
using Mosaic.Auth.Wpf;
using Mosaic.Contracts;
using Mosaic.FloatingWindows.Sqlite;
using Mosaic.FloatingWindows.Wpf;
using Mosaic.Maintenance.Contracts;
using Mosaic.Maintenance.Windows;
using Mosaic.Maintenance.Wpf;
using Mosaic.ReleaseSummary;
using System.Reflection;
using Mosaic.Presentation.Contracts;
using Mosaic.Settings.Core;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Sqlite;
using Mosaic.Settings.Wpf;
using Mosaic.Storage.Contracts;
using Mosaic.Storage.Sqlite;
using Mosaic.Sync.Core;
using Mosaic.Sync.Http;
using Mosaic.Tasks.Core;
using Mosaic.Tasks.Sqlite;
using Mosaic.Tasks.Wpf;
using Mosaic.DesktopPresence.Contracts;
using Mosaic.DesktopPresence.Wpf;

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
    internal bool ConfigurationExisted { get; init; } = true;
    internal SettingsPageRegistry SettingsPages { get; } = new();
    internal SqliteModule? Storage { get; set; }
    internal TasksSqliteModule? Repository { get; set; }
    internal TasksModule? Tasks { get; set; }
    internal SettingsSqliteModule? SettingsRepository { get; set; }
    internal SettingsModule? Settings { get; set; }
    internal IHostSettingSource? HostSettings { get; init; }
    internal required IDesktopWindowHost WindowHost { get; init; }
    internal ICompanionViewFactory? CompanionFactory { get; set; }
    internal IDesktopPresenceApi? Presence { get; set; }
    internal IReleaseSummaryApi? ReleaseSummary { get; set; }
    internal FloatingWindowsSqliteModule? FloatingStorage {get;set;}
    internal FloatingWindowsModule? Floating {get;set;}
    internal IFloatingContentFactory? FloatingFactory {get;set;}
    internal IFloatingDetachSource? DetachSource {get;set;}
    internal HttpAuthModule? AuthTransport { get; set; }
    internal AuthWindowsModule? AuthSecrets { get; set; }
    internal AuthModule? Auth { get; set; }
    internal HttpSyncModule? SyncTransport { get; set; }
    internal SyncModule? Sync { get; set; }
    internal WindowsMaintenanceService? Maintenance { get; set; }
    internal IApplicationLifecycle? Lifecycle { get; init; }
}

internal static class BuiltInModuleCatalog
{
    private sealed record Entry(ModuleManifest Manifest, Func<ModuleRuntimeContext, IModuleActivation> Create);

    private static readonly IReadOnlyDictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal)
    {
        [SqliteModule.Definition.Id] = new(SqliteModule.Definition, context =>
        {
            var databasePath = context.Paths.Database(context.StorageConfiguration);
            context.Storage = new(new(
                databasePath,
                StorageIntSetting(context.HostSettings, "storage.queueCapacity"),
                StorageIntSetting(context.HostSettings, "storage.busyTimeoutSeconds"),
                StorageIntSetting(context.HostSettings, "storage.backupRetention"),
                context.HostSettings?.GetValue("storage.journalMode") as string == "Wal" ? SqliteJournalMode.Wal : SqliteJournalMode.Delete,
                context.HostSettings?.GetValue("storage.connectionMode") as string == "PooledPerOperation" ? SqliteConnectionMode.PooledPerOperation : SqliteConnectionMode.ReusedSingle,
                context.ConfigurationExisted),
                context.Diagnostics,
                new SqliteStorageRelocation(databasePath, context.Paths.ConfigDirectory));
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
                context.Settings?.Api,
                context.Repository?.TransferRepository));
            return context.Tasks;
        }),
        [TasksUiModule.Definition.Id] = new(TasksUiModule.Definition, context =>
        {
            var module = new TasksUiModule(new(
                context.Tasks?.Api ?? throw new InvalidOperationException("Task API was not assembled before task UI."),
                context.Dispatcher,
                context.Dialogs,
                context.Diagnostics,
                context.Settings?.Api, context.Tasks?.Changes));
            context.FloatingFactory=module.FloatingFactory;
            context.DetachSource=module.DetachSource;
            context.CompanionFactory = module.CompanionFactory;
            return module;
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
        [DesktopPresenceModule.Definition.Id] = new(DesktopPresenceModule.Definition, context =>
        {
            var module = new DesktopPresenceModule(new(context.WindowHost,
                context.CompanionFactory ?? throw new InvalidOperationException("Task companion capability was not assembled."),
                context.Settings?.Api ?? throw new InvalidOperationException("Settings capability was not assembled."),
                context.Dispatcher, context.Diagnostics));
            context.Presence = module.Api;
            return module;
        }),
        [ReleaseSummaryModule.Definition.Id] = new(ReleaseSummaryModule.Definition, context =>
        {
            var assembly = typeof(BuiltInModuleCatalog).Assembly;
            var published = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(item => item.Key == "MosaicPublishedBuild" && item.Value == "true");
            var path = Path.Combine(AppContext.BaseDirectory, "release-manifest.json");
            var input = new ReleaseSummaryStartupInput(File.Exists(path) ? File.ReadAllText(path) : "", assembly.GetName().Version!.ToString(3), published);
            var module = new ReleaseSummaryModule(new(context.Tasks!.CreateOnceApi, context.Settings!.Api, context.Diagnostics, input));
            context.ReleaseSummary = module.Api;
            return module;
        }),
        [FloatingWindowsSqliteModule.Definition.Id]=new(FloatingWindowsSqliteModule.Definition,context=>
        {
            context.FloatingStorage=new(context.Storage!.Session);return context.FloatingStorage;
        }),
        [FloatingWindowsModule.Definition.Id]=new(FloatingWindowsModule.Definition,context=>
        {
            context.Floating=new(new(context.FloatingStorage!.Repository,context.FloatingFactory!,context.DetachSource!,context.Settings!.Api,context.Dispatcher,context.Diagnostics,async()=>
            {
                if(context.Presence is {} presence){var restored=await presence.RequestRestoreAsync();if(!restored.IsSuccess)throw new InvalidOperationException(restored.Error!.Message);}
                else await context.WindowHost.RestoreMainWindowAsync();
            }));return context.Floating;
        }),
        [SettingsUiModule.Definition.Id] = new(SettingsUiModule.Definition, context =>
        {
            return new SettingsUiModule(new(
                context.Settings?.Api ?? throw new InvalidOperationException("Settings API was not assembled before settings UI."),
                context.Dispatcher,
                context.Dialogs,
                context.Diagnostics,
                context.SettingsPages));
        }),
        [HttpAuthModule.Definition.Id] = new(HttpAuthModule.Definition, context =>
        {
            context.AuthTransport = new(context.Clock);
            return context.AuthTransport;
        }),
        [AuthWindowsModule.Definition.Id] = new(AuthWindowsModule.Definition, context =>
        {
            context.AuthSecrets = new(context.Paths.AuthSecrets);
            return context.AuthSecrets;
        }),
        [AuthModule.Definition.Id] = new(AuthModule.Definition, context =>
        {
            context.Auth = new(new(
                context.AuthTransport?.Transport ?? throw new InvalidOperationException("Account transport was not assembled before account core."),
                context.AuthSecrets?.Secrets ?? throw new InvalidOperationException("Account secret store was not assembled before account core."),
                context.Settings?.Api ?? throw new InvalidOperationException("Settings core was not assembled before account core."),
                context.Clock,
                new Mosaic.Kernel.ModuleExecutionGate(),
                context.Diagnostics));
            return context.Auth;
        }),
        [HttpSyncModule.Definition.Id] = new(HttpSyncModule.Definition, context =>
        {
            context.SyncTransport = new(context.Clock);
            return context.SyncTransport;
        }),
        [SyncModule.Definition.Id] = new(SyncModule.Definition, context =>
        {
            context.Sync = new(new(
                context.Auth?.Api ?? throw new InvalidOperationException("Account API was not assembled before backup core."),
                context.SyncTransport?.Transport ?? throw new InvalidOperationException("Backup transport was not assembled before backup core."),
                context.Tasks?.Transfer ?? throw new InvalidOperationException("Task transfer was not assembled before backup core."),
                context.Settings?.Api ?? throw new InvalidOperationException("Settings core was not assembled before backup core."),
                new Mosaic.Kernel.ModuleExecutionGate(),
                context.Diagnostics));
            return context.Sync;
        }),
        [AuthUiModule.Definition.Id] = new(AuthUiModule.Definition, context =>
        {
            return new AuthUiModule(new(
                context.Auth?.Api ?? throw new InvalidOperationException("Account API was not assembled before account UI."),
                context.Sync?.Api ?? throw new InvalidOperationException("Backup API was not assembled before account UI."),
                context.Settings?.Api ?? throw new InvalidOperationException("Settings API was not assembled before account UI."),
                context.Dispatcher,
                context.Dialogs,
                context.Diagnostics,
                context.SettingsPages));
        }),
        [MaintenanceUiModule.Definition.Id] = new(MaintenanceUiModule.Definition, context =>
        {
            var relocation = context.Storage?.GetCapability(new("storage.relocation", 1)) as IStorageRelocation
                ?? throw new InvalidOperationException("Storage relocation capability was not assembled before maintenance UI.");
            context.Maintenance = new(context.Paths.Root);
            return new MaintenanceUiModule(new(
                relocation,
                context.Maintenance,
                context.Lifecycle ?? throw new InvalidOperationException("Application lifecycle was not supplied to maintenance UI."),
                context.Dialogs,
                context.Diagnostics,
                context.SettingsPages,
                new DesktopConfigurationHealthCheck(context.Paths)));
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
