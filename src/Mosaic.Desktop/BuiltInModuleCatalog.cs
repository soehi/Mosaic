using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
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
    internal SqliteModule? Storage { get; set; }
    internal TasksSqliteModule? Repository { get; set; }
    internal TasksModule? Tasks { get; set; }
}

internal static class BuiltInModuleCatalog
{
    private sealed record Entry(ModuleManifest Manifest, Func<ModuleRuntimeContext, IModuleActivation> Create);

    private static readonly IReadOnlyDictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal)
    {
        [SqliteModule.Definition.Id] = new(SqliteModule.Definition, context =>
        {
            context.Storage = new(new(context.Paths.Database(context.StorageConfiguration.Filename)), context.Diagnostics);
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
                context.Diagnostics));
            return context.Tasks;
        }),
        [TasksUiModule.Definition.Id] = new(TasksUiModule.Definition, context =>
        {
            return new TasksUiModule(new(
                context.Tasks?.Api ?? throw new InvalidOperationException("Task API was not assembled before task UI."),
                context.Dispatcher,
                context.Dialogs,
                context.Diagnostics));
        })
    };

    internal static IReadOnlyDictionary<string, ModuleManifest> Definitions { get; } =
        Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Manifest, StringComparer.Ordinal);

    internal static IModuleActivation Create(ModuleRuntimeContext context, ModuleManifest manifest) =>
        Entries.TryGetValue(manifest.Id, out var entry)
            ? entry.Create(context)
            : throw new ConfigurationException("未知模块。");
}
