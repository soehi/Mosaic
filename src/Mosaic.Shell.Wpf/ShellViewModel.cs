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
    private IDisposable? _settingsRegistration;
    private IDisposable? _localizationRegistration;
    private Mosaic.Settings.Contracts.ISettingsCore? _settings;
    internal ShellViewModel(ContributionRegistry registry, IUserDialogService dialogs, Mosaic.Settings.Contracts.ISettingsCore? settings)
    {
        _navigation = new(registry, dialogs); _registry = registry; _dialogs = dialogs; Navigation = registry.Navigation;
        NavigateCommand = new AsyncRelayCommand<NavigationItem>(NavigateAsync);
        registry.Changed += OnContributionsChanged;
        _settings = settings;
        if (Application.Current is null)
        {
            _localizationRegistration = null;
        }
        else
        {
            _localizationRegistration = MosaicLocalizationManager.Register(
            "shell",
                language => ShellLocalization.Dictionary(language == MosaicLanguage.English));
        }
        _settingsRegistration = settings?.Register(new ShellSettingSource(ApplyThemeSetting));
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
            if (values.IsSuccess && values.Value is { } current) ApplyThemeSetting(current);
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
        _settingsRegistration?.Dispose();
        _localizationRegistration?.Dispose();
        await _removePage;
        await _navigation.DisposeAsync();
    }

    private static void ApplyThemeSetting(IReadOnlyDictionary<string, object?> values)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => ApplyThemeSetting(values));
            return;
        }

        var value = values.TryGetValue("appearance.theme", out var setting) ? setting as string : null;
        var variant = value switch
        {
            "Light" => MosaicThemeVariant.Light,
            "Dark" => MosaicThemeVariant.Dark,
            "HighContrast" => MosaicThemeVariant.HighContrast,
            _ => (MosaicThemeVariant?)null
        };

        if (variant is { } fixedVariant) MosaicThemeManager.Apply(resources, fixedVariant);
        else MosaicThemeManager.Attach(resources);

        var languageValue = values.TryGetValue("ui.language", out var language) ? language as string : null;
        var resolvedLanguage = languageValue switch
        {
            "Chinese" => MosaicLanguage.Chinese,
            "English" => MosaicLanguage.English,
            _ => MosaicLanguage.FollowSystem
        };
        MosaicLocalizationManager.SetLanguage(resolvedLanguage);
    }

    private sealed class ShellSettingSource(Action<IReadOnlyDictionary<string, object?>> apply) : Mosaic.Settings.Contracts.ISettingSource
    {
        public string ModuleId => "shell";
        public IReadOnlyList<Mosaic.Settings.Contracts.SettingDefinition> Definitions { get; } =
        [
            new("appearance.theme", "shell", "主题", "界面配色跟随系统或固定主题", Mosaic.Settings.Contracts.SettingValueKind.Enum, "FollowSystem", null, null, ["FollowSystem", "Light", "Dark", "HighContrast"], Mosaic.Settings.Contracts.SettingCategory.Normal, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
            new("ui.language", "shell", "界面语言", "选择中文或英文界面", Mosaic.Settings.Contracts.SettingValueKind.Enum, "FollowSystem", null, null, ["FollowSystem", "Chinese", "English"], Mosaic.Settings.Contracts.SettingCategory.Normal, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
            new("shell.sidebar.collapseBelowWidth", "shell", "侧栏折叠宽度", "窗口小于该宽度时折叠侧栏", Mosaic.Settings.Contracts.SettingValueKind.Integer, 1040, 720, 1600, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
            new("shell.sidebar.expandAtWidth", "shell", "侧栏展开宽度", "窗口达到该宽度时重新展开", Mosaic.Settings.Contracts.SettingValueKind.Integer, 1060, 740, 1800, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
            new("shell.sidebar.fullWidth", "shell", "完整侧栏宽度", "展开状态的侧栏宽度", Mosaic.Settings.Contracts.SettingValueKind.Integer, 190, 160, 260, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
            new("shell.sidebar.railWidth", "shell", "图标栏宽度", "折叠状态的侧栏宽度", Mosaic.Settings.Contracts.SettingValueKind.Integer, 64, 48, 88, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
            new("shell.page.marginLeft", "shell", "页面左边距", "任务等内容页左侧边距", Mosaic.Settings.Contracts.SettingValueKind.Integer, 32, 8, 64, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
            new("shell.page.marginTop", "shell", "页面上边距", "任务等内容页顶部边距", Mosaic.Settings.Contracts.SettingValueKind.Integer, 30, 8, 64, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
            new("shell.page.marginRight", "shell", "页面右边距", "任务等内容页右侧边距", Mosaic.Settings.Contracts.SettingValueKind.Integer, 28, 8, 64, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
            new("shell.page.marginBottom", "shell", "页面下边距", "任务等内容页底部边距", Mosaic.Settings.Contracts.SettingValueKind.Integer, 28, 8, 64, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings)
        ];
        public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
        {
            apply(values);
            return Task.CompletedTask;
        }
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
