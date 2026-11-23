using System.Windows;
using System.Windows.Threading;
using System.Collections.ObjectModel;
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
    private string? _pageError;
    private string? _currentNavigationId;
    private string? _currentViewId;
    private string? _currentNavigationLabel;
    private string? _currentNavigationLabelKey;
    private IDisposable? _localizationRegistration;
    private Mosaic.Settings.Contracts.ISettingsApi? _settings;
    private readonly ObservableCollection<NavigationItem> _navigationItems = [];
    private readonly ObservableCollection<IStatusContribution> _statusItems = [];
    // Contributions that opt into the sidebar's bottom area; the same items stay in the
    // status bar and keep their TargetViewId navigation. Modules declare placement, the shell
    // never inspects what a contribution means.
    private readonly ObservableCollection<IStatusContribution> _sidebarStatusItems = [];
    private readonly Dictionary<IStatusContribution, EventHandler> _statusActionHandlers = new(ReferenceEqualityComparer.Instance);
    internal IReadOnlyDictionary<string, object?>? InitialValues { get; private set; }
    internal ShellViewModel(ContributionRegistry registry, IUserDialogService dialogs, Mosaic.Settings.Contracts.ISettingsApi? settings)
    {
        _navigation = new(registry, dialogs); _registry = registry; _dialogs = dialogs;
        NavigateCommand = new AsyncRelayCommand<NavigationItem>(NavigateAsync);
        NavigateToViewCommand = new AsyncRelayCommand<string?>(NavigateToViewAsync);
        StatusActionCommand = new RelayCommand<IStatusContribution>(item => item?.RequestAction());
        registry.Changed += OnContributionsChanged;
        _settings = settings;
        _localizationRegistration = Application.Current is null ? null : MosaicLocalizationManager.Register(
            "shell",
            language => ShellLocalization.Dictionary(language == MosaicLanguage.English));
        Navigation = new ReadOnlyObservableCollection<NavigationItem>(_navigationItems);
        StatusItems = new ReadOnlyObservableCollection<IStatusContribution>(_statusItems);
        SidebarStatusItems = new ReadOnlyObservableCollection<IStatusContribution>(_sidebarStatusItems);
        RebuildNavigationItems();
        RebuildStatusItems();
        MosaicLocalizationManager.LanguageChanged += OnLanguageChanged;
    }
    public IReadOnlyList<NavigationItem> Navigation { get; }
    public IReadOnlyList<IStatusContribution> StatusItems { get; }
    public IReadOnlyList<IStatusContribution> SidebarStatusItems { get; }
    public FrameworkElement? Content { get => _content; private set => SetProperty(ref _content, value); }
    public string PageTitle { get => _pageTitle; private set => SetProperty(ref _pageTitle, value); }
    // Set when a page failed to load so the empty workspace does not claim success.
    public string? PageError
    {
        get => _pageError;
        private set
        {
            if (!SetProperty(ref _pageError, value)) return;
            OnPropertyChanged(nameof(HasPageError));
        }
    }
    public bool HasPageError => _pageError is not null;
    public string? CurrentNavigationId { get => _currentNavigationId; private set => SetProperty(ref _currentNavigationId, value); }
    public IAsyncRelayCommand<NavigationItem> NavigateCommand { get; }
    public IAsyncRelayCommand<string?> NavigateToViewCommand { get; }
    // Forwards a sidebar status action back to the owning contribution; the shell never learns
    // what the action means.
    public IRelayCommand<IStatusContribution> StatusActionCommand { get; }
    internal async Task InitializeAsync()
    {
        if (_settings is { } settings)
        {
            var values = await settings.GetValuesAsync().ConfigureAwait(true);
            if (values.IsSuccess && values.Value is { } current)
            {
                InitialValues = current;
                await ShellSettingsApplier.ApplyAsync(current);
            }
        }
        // 启动页由导航贡献显式声明（IsDefault），不再依赖加载顺序或首次运行状态。
        if ((Navigation.FirstOrDefault(candidate => candidate.IsDefault) ?? Navigation.FirstOrDefault()) is { } item)
            await NavigateAsync(item);
        else
            PageError = ShellLocalization.Localize("Shell.Error.PageLoad", "页面加载失败，请重新启动应用。");
    }
    internal static string? ReadString(IReadOnlyDictionary<string, object?> values, string id) =>
        values.TryGetValue(id, out var raw) ? raw as string : null;
    private async Task NavigateAsync(NavigationItem? item)
    {
        if (item is null) return;
        try
        {
            if (!await _navigation.NavigateAsync(item.ViewId)) return;
            Content = _navigation.Current!.View;
            PageError = null;
            _currentViewId = item.ViewId;
            _currentNavigationLabel = item.Label;
            _currentNavigationLabelKey = item.LabelKey;
            PageTitle = TranslateNavigationLabel(item.LabelKey, item.Label);
            CurrentNavigationId = item.Id;
        }
        catch (Exception)
        {
            PageError = ShellLocalization.Localize("Shell.Error.PageLoad", "页面加载失败，请重新启动应用。");
            await _dialogs.ShowErrorAsync(PageError);
        }
    }
    private async Task NavigateToViewAsync(string? viewId)
    {
        if (string.IsNullOrWhiteSpace(viewId)) return;
        var item = Navigation.FirstOrDefault(candidate => string.Equals(candidate.ViewId, viewId, StringComparison.Ordinal));
        if (item is not null) await NavigateAsync(item);
        else await NavigateDirectAsync(viewId);
    }

    // Opens a view that has no navigation entry (e.g. the account page). The page content swaps
    // in without a navigation highlight; the title falls back to the shell default.
    private async Task NavigateDirectAsync(string viewId)
    {
        try
        {
            if (!await _navigation.NavigateAsync(viewId)) return;
            Content = _navigation.Current!.View;
            PageError = null;
            _currentViewId = viewId;
            _currentNavigationLabel = null;
            _currentNavigationLabelKey = null;
            PageTitle = ShellLocalization.Localize("Shell.PageTitle", "工作空间");
            CurrentNavigationId = null;
        }
        catch (Exception)
        {
            PageError = ShellLocalization.Localize("Shell.Error.PageLoad", "页面加载失败，请重新启动应用。");
            await _dialogs.ShowErrorAsync(PageError);
        }
    }
    private void OnContributionsChanged(object? sender, EventArgs e)
    {
        RebuildNavigationItems();
        RebuildStatusItems();
        if (_navigation.CurrentViewId is { } currentViewId && _registry.Find(currentViewId) is null)
        {
            Content = null;
            PageTitle = ShellLocalization.Localize("Shell.PageTitle", "工作空间");
            CurrentNavigationId = null;
            var release = _navigation.ReleaseCurrentAsync();
            _removePage = ReleaseRemovedPageAsync(_removePage, release);
        }
        if (_navigation.Current is null && (Navigation.FirstOrDefault(candidate => candidate.IsDefault) ?? Navigation.FirstOrDefault()) is { } fallback)
            NavigateCommand.Execute(fallback);
    }
    private async Task ReleaseRemovedPageAsync(Task previous, ValueTask release)
    {
        await previous;
        try { await release; }
        catch (Exception)
        {
            await _dialogs.ShowErrorAsync(ShellLocalization.Localize(
                "Shell.Error.PageClose", "页面关闭未完成，请重新启动应用。"));
        }
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
        MosaicLocalizationManager.LanguageChanged -= OnLanguageChanged;
        foreach (var (item, handler) in _statusActionHandlers)
            item.ActionRequested -= handler;
        _statusActionHandlers.Clear();
        _localizationRegistration?.Dispose();
        await _removePage;
        await _navigation.DisposeAsync();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RebuildNavigationItems();
        if (_currentNavigationLabel is { } label)
            PageTitle = TranslateNavigationLabel(_currentNavigationLabelKey, label);
    }

    private void RebuildNavigationItems()
    {
        _navigationItems.Clear();
        foreach (var item in _registry.Navigation)
            _navigationItems.Add(new NavigationItem(
                item.Id,
                TranslateNavigationLabel(item.LabelKey, item.Label),
                item.IconKey,
                item.ViewId,
                item.Order,
                item.LabelKey,
                item.IsDefault));
    }

    private void RebuildStatusItems()
    {
        foreach (var (item, handler) in _statusActionHandlers)
            item.ActionRequested -= handler;
        _statusActionHandlers.Clear();
        _statusItems.Clear();
        _sidebarStatusItems.Clear();
        foreach (var item in _registry.Status)
        {
            _statusItems.Add(item);
            if (item.SidebarPlacement == StatusPlacement.Bottom)
            {
                _sidebarStatusItems.Add(item);
                // A completed action (e.g. sign-out finished) may want the sidebar's target
                // page on screen; the contribution only signals, the shell navigates generically.
                EventHandler handler = (_, _) =>
                {
                    if (item.TargetViewId is { } target && !string.Equals(_navigation.CurrentViewId, target, StringComparison.Ordinal))
                        _ = NavigateToViewAsync(target);
                };
                _statusActionHandlers[item] = handler;
                item.ActionRequested += handler;
            }
        }
    }

    private static string TranslateNavigationLabel(string? labelKey, string label) =>
        labelKey is null ? label : ShellLocalization.Localize(labelKey, label);
}
