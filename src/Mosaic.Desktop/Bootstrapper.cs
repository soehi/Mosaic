using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mosaic.Contracts;
using Mosaic.Kernel;
using Mosaic.Presentation.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Shell.Wpf;
using Mosaic.DesktopPresence.Contracts;
using Mosaic.Maintenance.Contracts;

namespace Mosaic.Desktop;

internal sealed class Bootstrapper : IAsyncDisposable
{
    private readonly ModuleRuntime _runtime = new();
    private readonly ContributionRegistry _contributions = new();
    private readonly Dictionary<IModuleActivation, IDisposable> _uiRegistrations = [];
    private IDisposable? _shellSettingsRegistration;
    private ISettingsCore? _settingsCore;
    private IHost? _host;
    private bool _disposed;
    internal Mosaic.FloatingWindows.Wpf.FloatingWindowsModule? Floating {get;private set;}
    internal IDesktopPresenceApi? Presence { get; private set; }
    internal Bootstrapper()
    {
        _runtime.ModuleStarted += OnModuleStarted;
        _runtime.ModuleStopping += OnModuleStopping;
    }
    internal async Task<ShellWindow> StartAsync(AppPaths paths, IUiDispatcher dispatcher, IUserDialogService dialogs, IDesktopWindowHost windowHost, bool? configurationExistedAtStartup = null, IApplicationLifecycle? lifecycle = null)
    {
        var configurationExisted = configurationExistedAtStartup ?? File.Exists(paths.ConfigPath);
        var configuration = AppConfiguration.Load(paths);
        var hostSettings = new HostSettingSource(paths, configuration);
        await StartupStorageRelocation.RunAsync(paths, configuration, hostSettings);
        var storage = new StorageConfiguration(
            "sqlite",
            hostSettings.GetValue("storage.location") as string ?? configuration.Storage.Location,
            hostSettings.GetValue("storage.filename") as string ?? configuration.Storage.Filename);
        if (storage.Location != "local-app-data" && !Path.IsPathFullyQualified(storage.Location))
            throw new ConfigurationException("存储位置必须是 local-app-data 或绝对路径。");
        if (!AppConfiguration.IsValidDatabaseFilename(storage.Filename))
            throw new ConfigurationException("存储配置无效，数据库名称必须是合法的单个文件名。");
        var configuredModules = hostSettings.GetValue("host.modules") as string[] ?? configuration.Modules;
        var configuredProviders = hostSettings.GetValue("host.providers") as IReadOnlyDictionary<string, string> ?? configuration.Providers;
        var semantic = await hostSettings.ValidateAsync(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["host.modules"] = configuredModules,
            ["host.providers"] = configuredProviders
        });
        if (!semantic.IsSuccess) throw new ConfigurationException("模块或能力绑定配置无效：" + semantic.Error!.Message);
        IReadOnlyList<ModuleManifest> ordered;
        try { ordered = ModuleGraph.Resolve(configuredModules.Select(id => BuiltInModuleCatalog.Definitions[id]).ToArray(), configuredProviders); }
        catch (InvalidOperationException ex) { throw new ConfigurationException("模块依赖校验失败：" + ex.Message); }
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(paths.Logs, ResolveLogLevel(hostSettings), ResolveLogRetention(hostSettings)));
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<IIdGenerator, IdGenerator>();
        builder.Services.AddSingleton<IDiagnosticSink, DiagnosticSink>();
        _host = builder.Build(); await _host.StartAsync();
        var diagnostics = _host.Services.GetRequiredService<IDiagnosticSink>();
        var context = new ModuleRuntimeContext(
            paths,
            storage,
            _host.Services.GetRequiredService<TimeProvider>(),
            _host.Services.GetRequiredService<IIdGenerator>(),
            diagnostics,
            dispatcher,
            dialogs)
        {
            HostSettings = hostSettings,
            ConfigurationExisted = configurationExisted,
            WindowHost = windowHost,
            Lifecycle = lifecycle
        };
        try
        {
            await _runtime.StartAsync(ordered, manifest => BuiltInModuleCatalog.Create(context, manifest), CancellationToken.None, async (module, token) =>
            {
                if (module is not Mosaic.Auth.Core.AuthModule) return;
                var installationState = _runtime.Resolve<Mosaic.Storage.Contracts.IInstallationState>(new("startup.installation-state", 1), configuredProviders);
                var classification = await installationState.GetInstallationKindAsync(token);
                if (!classification.IsSuccess) throw new InvalidOperationException("Cannot determine installation state: " + classification.Error?.Message);
                var repository = _runtime.Resolve<ISettingRepository>(new("settings.repository", 1), configuredProviders);
                var initialization = Mosaic.Auth.Core.AuthModule.InitialAccountChoice(classification.Value == Mosaic.Storage.Contracts.InstallationKind.Legacy);
                var initialized = await repository.InitializeAsync(initialization, token);
                if (!initialized.IsSuccess) throw new InvalidOperationException("Settings initialization failed: " + initialized.Error?.Message);
            });
            Presence = context.Presence;
            Floating=context.Floating;
            if (context.ReleaseSummary is { } summary) await summary.RequestStartupReadyAsync();
            if (Floating is {} floating) await floating.RestoreAsync();
            var settingsApi = context.Settings?.Api;
            _settingsCore = settingsApi;
            _shellSettingsRegistration = settingsApi?.Register(new ShellSettingSource());
            return new ShellWindow(_contributions, dialogs, settingsApi, context.SettingsPages);
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
        if (_settingsCore is not null && _shellSettingsRegistration is not null)
        {
            var ready = await _settingsCore.PrepareSourceStopAsync("shell");
            if (!ready.IsSuccess) throw new InvalidOperationException(ready.Error!.Message);
        }
        _shellSettingsRegistration?.Dispose();
        _shellSettingsRegistration = null;
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
        if (module is IUiModule ui) _uiRegistrations[module] = _contributions.Register(ui.Navigation, ui.Views, ui.Status);
    }
    private void OnModuleStopping(IModuleActivation module)
    {
        if (_uiRegistrations.Remove(module, out var registration)) registration.Dispose();
    }
    private static LogLevel ResolveLogLevel(IHostSettingSource hostSettings) =>
        (hostSettings.GetValue("logging.level") as string) switch
        {
            "Debug" => LogLevel.Debug,
            "Warning" => LogLevel.Warning,
            "Error" => LogLevel.Error,
            "None" => LogLevel.None,
            _ => LogLevel.Information
        };
    private static int ResolveLogRetention(IHostSettingSource hostSettings) =>
        hostSettings.GetValue("logging.retentionDays") is int days ? days : 0;
    private sealed class IdGenerator : IIdGenerator { public Guid NewId() => Guid.NewGuid(); }
}
