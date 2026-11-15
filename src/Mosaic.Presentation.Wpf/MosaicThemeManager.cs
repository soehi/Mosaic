using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace Mosaic.Presentation.Wpf;

public enum MosaicThemeVariant
{
    Light,
    Dark,
    HighContrast
}

public static class MosaicThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly object Gate = new();
    private static ResourceDictionary? _attachedResources;
    private static ResourceDictionary? _activePalette;
    private static bool _observing;
    private static bool _followSystem = true;
    private static MosaicThemeVariant _fixedVariant = MosaicThemeVariant.Light;

    public static void Attach(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        lock (Gate)
        {
            _attachedResources = resources;
            ApplyCore(resources, DetectSystemVariant());
            _followSystem = true;
            if (_observing) return;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _observing = true;
        }
    }

    public static void Apply(ResourceDictionary resources, MosaicThemeVariant variant)
    {
        ArgumentNullException.ThrowIfNull(resources);
        lock (Gate)
        {
            _attachedResources = resources;
            _followSystem = false;
            _fixedVariant = variant;
            ApplyCore(resources, variant);
        }
    }

    private static void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
            Refresh();
    }

    private static void Refresh()
    {
        ResourceDictionary? resources;
        lock (Gate) resources = _attachedResources;
        if (resources is null) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => Refresh());
            return;
        }
        lock (Gate)
        {
            if (!ReferenceEquals(_attachedResources, resources)) return;
            ApplyCore(resources, _followSystem ? DetectSystemVariant() : _fixedVariant);
        }
    }

    private static void ApplyCore(ResourceDictionary resources, MosaicThemeVariant variant)
    {
        if (_activePalette is not null) resources.MergedDictionaries.Remove(_activePalette);
        var fileName = variant switch
        {
            MosaicThemeVariant.Dark => "Dark.xaml",
            MosaicThemeVariant.HighContrast => "HighContrast.xaml",
            _ => "Light.xaml"
        };
        _activePalette = new()
        {
            Source = new Uri($"/Mosaic.Presentation.Wpf;component/Themes/Palettes/{fileName}", UriKind.Relative)
        };
        resources.MergedDictionaries.Add(_activePalette);
    }

    private static MosaicThemeVariant DetectSystemVariant()
    {
        if (SystemParameters.HighContrast) return MosaicThemeVariant.HighContrast;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? MosaicThemeVariant.Dark
                : MosaicThemeVariant.Light;
        }
        catch (Exception)
        {
            return MosaicThemeVariant.Light;
        }
    }
}
