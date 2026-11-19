using Mosaic.Mobile.App.Services;
using Mosaic.Mobile.Presentation;

namespace Mosaic.Mobile.App;

public partial class MosaicApp : Application
{
    private readonly StartupService _startup;
    private readonly AppThemeService _themeService;
    private readonly AppShell _shell;

    public MosaicApp(
        StartupService startup,
        AppThemeService themeService,
        AppShell shell)
    {
        InitializeComponent();
        _startup = startup;
        _themeService = themeService;
        _shell = shell;

        ApplyTheme(themeService.IsDark);
        themeService.Changed += (_, _) => ApplyTheme(_themeService.IsDark);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_shell);
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
