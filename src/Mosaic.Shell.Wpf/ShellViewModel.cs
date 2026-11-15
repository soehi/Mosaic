using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Presentation.Wpf;
using Mosaic.Presentation.Contracts;

namespace Mosaic.Shell.Wpf;

internal sealed class ShellViewModel : ObservableObject, IAsyncDisposable
{
    private readonly NavigationService _navigation;
    private readonly ContributionRegistry _registry;
    private readonly IUserDialogService _dialogs;
    private Task _removePage = Task.CompletedTask;
    private FrameworkElement? _content;
    private string _pageTitle = "工作空间";
    private string? _currentNavigationId;
    private IDisposable? _localizationRegistration;
    private Mosaic.Settings.Contracts.ISettingsApi? _settings;
    internal IReadOnlyDictionary<string, object?>? InitialValues { get; private set; }
    internal ShellViewModel(ContributionRegistry registry, IUserDialogService dialogs, Mosaic.Settings.Contracts.ISettingsApi? settings)
    {
        _navigation = new(registry, dialogs); _registry = registry; _dialogs = dialogs; Navigation = registry.Navigation;
        NavigateCommand = new AsyncRelayCommand<NavigationItem>(NavigateAsync);
        registry.Changed += OnContributionsChanged;
        _settings = settings;
        _localizationRegistration = Application.Current is null ? null : MosaicLocalizationManager.Register(
            "shell",
            language => ShellLocalization.Dictionary(language == MosaicLanguage.English));
    }
    public IReadOnlyList<NavigationItem> Navigation { get; }
    public FrameworkElement? Content { get => _content; private set => SetProperty(ref _content, value); }
    public string PageTitle { get => _pageTitle; private set => SetProperty(ref _pageTitle, value); }
    public string? CurrentNavigationId { get => _currentNavigationId; private set => SetProperty(ref _currentNavigationId, value); }
    public IAsyncRelayCommand<NavigationItem> NavigateCommand { get; }
    internal async Task InitializeAsync()
    {
        if (_settings is { } settings)
        {
            var values = await settings.GetValuesAsync().ConfigureAwait(true);
            if (values.IsSuccess && values.Value is { } current)
            {
                InitialValues = current;
                ShellSettingsApplier.Apply(current);
            }
        }
        if (Navigation.FirstOrDefault() is { } item) await NavigateAsync(item);
    }
    private async Task NavigateAsync(NavigationItem? item)
    {
        if (item is null) return;
        try
        {
            if (!await _navigation.NavigateAsync(item.ViewId)) return;
            Content = _navigation.Current!.View;
            PageTitle = TranslateNavigationLabel(item.Label);
            CurrentNavigationId = item.Id;
        }
        catch (Exception) { await _dialogs.ShowErrorAsync("页面加载失败，请重新启动应用。"); }
    }
    private void OnContributionsChanged(object? sender, EventArgs e)
    {
        if (_navigation.CurrentViewId is { } currentViewId && _registry.Find(currentViewId) is null)
        {
            Content = null;
            PageTitle = Translate("Shell.PageTitle", "工作空间");
            CurrentNavigationId = null;
            var release = _navigation.ReleaseCurrentAsync();
            _removePage = ReleaseRemovedPageAsync(_removePage, release);
        }
        if (_navigation.Current is null && Navigation.FirstOrDefault() is { } first)
            NavigateCommand.Execute(first);
    }
    private async Task ReleaseRemovedPageAsync(Task previous, ValueTask release)
    {
        await previous;
        try { await release; }
        catch (Exception) { await _dialogs.ShowErrorAsync("页面关闭未完成，请重新启动应用。"); }
    }
    internal async Task<bool> CanCloseAsync()
    {
        await _removePage;
        if (NavigateCommand.ExecutionTask is { IsCompleted: false } pending) await pending;
        return _navigation.Current is null || await _navigation.Current.Controller.CanLeaveAsync();
    }
    public async ValueTask DisposeAsync()
    {
        _registry.Changed -= OnContributionsChanged;
        _localizationRegistration?.Dispose();
        await _removePage;
        await _navigation.DisposeAsync();
    }

    private static string Translate(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as string ?? fallback;

    private static string TranslateNavigationLabel(string label) => label switch
    {
        "任务" => Translate("Tasks.Navigation", label),
        "设置" => Translate("Settings.Navigation", label),
        _ => Translate("Shell.PageTitle", label)
    };
}
