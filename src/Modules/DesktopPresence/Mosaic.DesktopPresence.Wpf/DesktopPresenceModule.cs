using System.IO;
using Mosaic.Contracts;
using Mosaic.DesktopPresence.Contracts;
using Mosaic.DesktopPresence.Core;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Settings.Contracts;

namespace Mosaic.DesktopPresence.Wpf;

public sealed record DesktopPresenceDependencies(IDesktopWindowHost Host, ICompanionViewFactory CompanionFactory,
    ISettingsCore Settings, IUiDispatcher Dispatcher, IDiagnosticSink Diagnostics,
    Func<string?>? ReadLegacyFilter = null);

public sealed class DesktopPresenceModule : IModuleActivation
{
    private readonly DesktopPresenceDependencies _dependencies;
    private readonly PresenceSurface _surface;
    private readonly PresenceController _controller;
    private readonly PresenceSettings _settings;
    private IDisposable? _registration;
    private IDisposable? _localization;
    public static ModuleManifest Definition { get; } = new("desktop-presence",
        [new("desktop.presence", 1)], [new("settings.api", 1), new("presence.tasks-view", 1)],
        "Owns desktop residence, edge entry and companion window lifecycle; never interprets task data.");
    public ModuleManifest Manifest => Definition;
    public IDesktopPresenceApi Api => _controller;

    public DesktopPresenceModule(DesktopPresenceDependencies dependencies)
    {
        _dependencies = dependencies;
        _surface = new(dependencies.Host, dependencies.CompanionFactory, dependencies.Settings, dependencies.Diagnostics);
        _controller = new(dependencies.Host, _surface, dependencies.Diagnostics);
        _surface.Api = _controller;
        _settings = new(async values =>
        {
            _controller.Configure(Enum.Parse<DesktopCloseBehavior>(PresenceSettings.ReadString(values, "desktop.closeBehavior", "Reside")));
            _controller.ConfigureShowTiming(ShowTiming(values));
            await _surface.UpdateSettingsAsync(values);
        }, _surface.MaximumOffset, dependencies.Dispatcher);
    }

    internal static MainWindowPresentation[] ShowTiming(IReadOnlyDictionary<string, object?> values)
    {
        var timing = PresenceSettings.ReadString(values, "desktop.edge.showTiming", "Resident");
        return timing switch
        {
            "Always" => [MainWindowPresentation.Visible, MainWindowPresentation.Minimized, MainWindowPresentation.Hidden],
            "Minimized" => [MainWindowPresentation.Minimized, MainWindowPresentation.Hidden],
            _ => [MainWindowPresentation.Hidden]
        };
    }

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        _localization = MosaicLocalizationManager.Register("desktop-presence", PresenceLocalization.Dictionary);
        _registration = _dependencies.Settings.Register(_settings);
        if (_dependencies.ReadLegacyFilter is { } readLegacyFilter)
        {
            string? legacyFilter;
            try { legacyFilter = readLegacyFilter()?.Trim(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _dependencies.Diagnostics.Report("desktop.presence.filter.read", exception);
                legacyFilter = null;
            }
            if (legacyFilter is "All" or "Open" or "Completed")
            {
                // 迁移失败不能阻断启动：旧值文件保留，下次启动会以同一迁移 ID 重试；
                // 本次使用注册定义的默认值（Unset → 回退默认筛选）。
                var migrated = await _dependencies.Settings.InitializeSettingAsync(
                    new("desktop.popup.lastFilter.v1", "desktop.popup.lastFilter", legacyFilter), cancellationToken);
                if (!migrated.IsSuccess)
                    _dependencies.Diagnostics.Report("desktop.presence.filter.migrate",
                        new InvalidOperationException(migrated.Error!.Message));
            }
        }
        var values = await _dependencies.Settings.GetValuesAsync(cancellationToken);
        if (!values.IsSuccess) throw new InvalidOperationException(values.Error!.Message);
        // Previously valid offsets are clamped at runtime when a monitor has been removed or resized.
        await _settings.ApplyAsync(values.Value, cancellationToken);
    }

    public async Task StopAsync()
    {
        if (_registration is not null)
        {
            var ready = await _dependencies.Settings.PrepareSourceStopAsync("desktop-presence");
            if (!ready.IsSuccess) throw new InvalidOperationException(ready.Error!.Message);
        }
        _registration?.Dispose(); _registration = null;
        var result = await _controller.StopAsync();
        _localization?.Dispose(); _localization = null;
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
