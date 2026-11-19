using Microsoft.Extensions.DependencyInjection;
using Mosaic.Mobile.App.Services;
using Mosaic.Mobile.Presentation;

namespace Mosaic.Mobile.App;

public partial class MosaicApp : Application
{
    private readonly StartupService _startup;
    private readonly AppThemeService _themeService;
    private readonly IServiceProvider _services;

    public MosaicApp(
        StartupService startup,
        AppThemeService themeService,
        IServiceProvider services)
    {
        // Resolve pages only after InitializeComponent has merged App.xaml
        // resources; constructing AppShell as a constructor dependency makes
        // MAUI load pages before StaticResource dictionaries exist.
        InitializeComponent();
        _startup = startup;
        _themeService = themeService;
        _services = services;

        ApplyTheme(themeService.IsDark);
        themeService.Changed += (_, _) => ApplyTheme(_themeService.IsDark);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var shell = _services.GetRequiredService<AppShell>();
        var window = new Window(shell);
        _ = _startup.InitializeAsync();
        return window;
    }

    protected override void OnSleep()
    {
        _startup.Pause();
        base.OnSleep();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _ = _startup.ResumeAsync();
    }

    private void ApplyTheme(bool dark)
    {
        UserAppTheme = dark ? AppTheme.Dark : AppTheme.Light;
    }
}
