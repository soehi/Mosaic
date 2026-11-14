using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mosaic.Contracts;
using Mosaic.Kernel;
using Mosaic.Presentation.Contracts;
using Mosaic.Shell.Wpf;

namespace Mosaic.Desktop;

internal sealed class Bootstrapper : IAsyncDisposable
{
    private readonly ModuleRuntime _runtime = new();
    private readonly ContributionRegistry _contributions = new();
    private readonly Dictionary<IModuleActivation, IDisposable> _uiRegistrations = [];
    private IHost? _host;
    private bool _disposed;
    internal Bootstrapper()
    {
        _runtime.ModuleStarted += OnModuleStarted;
        _runtime.ModuleStopping += OnModuleStopping;
    }
    internal async Task<ShellWindow> StartAsync(AppPaths paths, IUiDispatcher dispatcher, IUserDialogService dialogs)
    {
        var configuration = AppConfiguration.Load(paths);
        var hostSettings = new HostSettingSource(paths);
        IReadOnlyList<ModuleManifest> ordered;
        try { ordered = ModuleGraph.Resolve(configuration.Modules.Select(id => BuiltInModuleCatalog.Definitions[id]).ToArray(), configuration.Providers); }
        catch (InvalidOperationException ex) { throw new ConfigurationException("模块依赖校验失败：" + ex.Message); }
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders(); builder.Logging.AddProvider(new FileLoggerProvider(paths.Logs));
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<IIdGenerator, IdGenerator>();
        builder.Services.AddSingleton<IDiagnosticSink, DiagnosticSink>();
        _host = builder.Build(); await _host.StartAsync();
        var diagnostics = _host.Services.GetRequiredService<IDiagnosticSink>();
        var context = new ModuleRuntimeContext(
            paths,
            configuration.Storage,
            _host.Services.GetRequiredService<TimeProvider>(),
            _host.Services.GetRequiredService<IIdGenerator>(),
            diagnostics,
            dispatcher,
            dialogs)
        {
            HostSettings = hostSettings
        };
        try
        {
            await _runtime.StartAsync(ordered, manifest => BuiltInModuleCatalog.Create(context, manifest));
            return new ShellWindow(_contributions, dialogs, context.Settings?.Api);
        }
        catch (Exception)
        {
            try { await DisposeAsync(); }
            catch (Exception cleanupError)
            {
                var correlationId = Guid.NewGuid().ToString("N");
                try { diagnostics.Report(correlationId, cleanupError); } catch { }
            }
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var registration in _uiRegistrations.Values) registration.Dispose();
        _uiRegistrations.Clear();
        _contributions.Clear();
        try { await _runtime.DisposeAsync(); }
        finally
        {
            if (_host is not null)
            {
                try { await _host.StopAsync(); }
                finally { _host.Dispose(); }
            }
        }
    }
    private void OnModuleStarted(IModuleActivation module)
    {
        if (module is IUiModule ui) _uiRegistrations[module] = _contributions.Register(ui.Navigation, ui.Views);
    }
    private void OnModuleStopping(IModuleActivation module)
    {
        if (_uiRegistrations.Remove(module, out var registration)) registration.Dispose();
    }
    private sealed class IdGenerator : IIdGenerator { public Guid NewId() => Guid.NewGuid(); }
}
