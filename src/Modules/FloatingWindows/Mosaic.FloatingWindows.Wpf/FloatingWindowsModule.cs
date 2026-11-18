using Mosaic.Contracts;
using Mosaic.FloatingWindows.Contracts;
using Mosaic.FloatingWindows.Core;
using Mosaic.FloatingWindows.Persistence.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Settings.Contracts;
namespace Mosaic.FloatingWindows.Wpf;
public sealed record FloatingWindowsDependencies(IFloatingWindowRepository Repository,IFloatingContentFactory Content,
    IFloatingDetachSource Detach,ISettingsCore Settings,IUiDispatcher Dispatcher,IDiagnosticSink Diagnostics,Func<Task> RestoreMainWindow);
public sealed class FloatingWindowsModule : IModuleActivation
{
    private readonly FloatingWindowsDependencies _dependencies;
    private readonly FloatingSurface _surface;
    private readonly FloatingWindowCoordinator _coordinator;
    private IDisposable? _handler,_settings,_localization;
    private bool _stopped,_allowUnsavedExit;
    private bool _restoreOnStartup=true;
    public void AllowUnsavedExit(){_surface.Freeze();_allowUnsavedExit=true;_handler?.Dispose();_handler=null;}
    private IReadOnlyDictionary<string,object?> _values=new Dictionary<string,object?>();
    public FloatingWindowsModule(FloatingWindowsDependencies dependencies)
    {
        _dependencies=dependencies;
        _surface=new(dependencies.Content,dependencies.Dispatcher,dependencies.Diagnostics,dependencies.RestoreMainWindow);
        _coordinator=new(dependencies.Repository,_surface,()=>FloatingSettings.Read(_values,"floating.maxOpenWindows",8),dependencies.Diagnostics);
        _surface.Coordinator=_coordinator;
    }
    public static ModuleManifest Definition {get;}=new("floating-windows",[new("floating.windows",1)],
        [new("floating.repository",1),new("floating.task-content",1),new("floating.detach-source",1),new("settings.api",1)],"Owns independent floating window lifetime, restoration, geometry and settings.");
    public ModuleManifest Manifest=>Definition;
    public IFloatingWindowApi Api=>_coordinator;
    public async Task ActivateAsync(CancellationToken cancellationToken=default)
    {
        _localization=MosaicLocalizationManager.Register("floating-windows",FloatingLocalization.Dictionary);
        _settings=_dependencies.Settings.Register(new FloatingSettings(async values=>
        {
            _values=new Dictionary<string,object?>(values);await _dependencies.Dispatcher.InvokeAsync(()=>_surface.UpdateSettings(values));
        }));
        var values=await _dependencies.Settings.GetValuesAsync(cancellationToken);
        if(!values.IsSuccess)throw new InvalidOperationException(values.Error!.Message);
        _values=values.Value;_restoreOnStartup=!values.Value.TryGetValue("floating.restoreOnStartup",out var restore)||restore is not false;_surface.UpdateSettings(values.Value);
        _handler=_dependencies.Detach.RegisterDetachHandler(_coordinator);
    }
    public async Task RestoreAsync()
    {
        if(!_restoreOnStartup)return;
        try{await _coordinator.RestoreAsync();}catch(Exception ex){Report(ex);}
    }
    public async Task<OperationResult> PrepareExitAsync()
    {
        _handler?.Dispose();_handler=null;
        try
        {
            var result=await _coordinator.PrepareExitAsync(_surface.FlushAsync);
            if(!result.IsSuccess)await ResumeAsync();
            return result;
        }
        catch(Exception ex){Report(ex);await ResumeAsync();return OperationResult.Failure(new(ErrorCodes.StorageUnavailable,"无法保存小窗布局，请重试退出。"));}
    }
    private async Task ResumeAsync()
    {
        await _coordinator.ResumeAsync();
        _surface.Enable();
        _handler ??= _dependencies.Detach.RegisterDetachHandler(_coordinator);
    }
    public async Task StopAsync()
    {
        if(_stopped)return;
        if(_allowUnsavedExit)await _coordinator.StopWithoutSavingAsync();
        else {var prepared=await PrepareExitAsync();if(!prepared.IsSuccess)throw new InvalidOperationException(prepared.Error!.Message);await _coordinator.StopAsync();}
        var ready=await _dependencies.Settings.PrepareSourceStopAsync("floating-windows");if(!ready.IsSuccess)throw new InvalidOperationException(ready.Error!.Message);
        _settings?.Dispose();_settings=null;_stopped=true;
    }
    public async ValueTask DisposeAsync(){await StopAsync();_localization?.Dispose();_localization=null;_surface.Dispose();}
    private void Report(Exception ex){try{_dependencies.Diagnostics.Report(Guid.NewGuid().ToString("N"),ex);}catch{}}
}
