using System.Windows;
using System.Windows.Threading;
using Mosaic.Presentation.Wpf;
using Mosaic.Settings.Contracts;

namespace Mosaic.Shell.Wpf;

public sealed class ShellSettingSource : ISettingSource
{
    public string ModuleId => "shell";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new("appearance.theme", "shell", "主题", "界面配色跟随系统或固定主题", SettingValueKind.Enum, "FollowSystem", null, null, ["FollowSystem", "Light", "Dark", "HighContrast"], SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("ui.language", "shell", "界面语言", "选择中文或英文界面", SettingValueKind.Enum, "FollowSystem", null, null, ["FollowSystem", "Chinese", "English"], SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("shell.sidebar.collapseBelowWidth", "shell", "侧栏折叠宽度", "窗口小于该宽度时折叠侧栏", SettingValueKind.Integer, 1040, 720, 1600, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("shell.sidebar.expandAtWidth", "shell", "侧栏展开宽度", "窗口达到该宽度时重新展开", SettingValueKind.Integer, 1060, 740, 1800, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("shell.sidebar.fullWidth", "shell", "完整侧栏宽度", "展开状态的侧栏宽度", SettingValueKind.Integer, 190, 160, 260, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("shell.sidebar.railWidth", "shell", "图标栏宽度", "折叠状态的侧栏宽度", SettingValueKind.Integer, 64, 48, 88, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("shell.page.marginLeft", "shell", "页面左边距", "任务等内容页左侧边距", SettingValueKind.Integer, 32, 8, 64, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("shell.page.marginTop", "shell", "页面上边距", "任务等内容页顶部边距", SettingValueKind.Integer, 30, 8, 64, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("shell.page.marginRight", "shell", "页面右边距", "任务等内容页右侧边距", SettingValueKind.Integer, 28, 8, 64, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("shell.page.marginBottom", "shell", "页面下边距", "任务等内容页底部边距", SettingValueKind.Integer, 28, 8, 64, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings)
    ];

    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        ShellSettingsApplier.Apply(values);
        return Task.CompletedTask;
    }
}

internal static class ShellSettingsApplier
{
    private static ShellWindow? _current;

    internal static void Attach(ShellWindow window) => _current = window;

    internal static void Detach(ShellWindow window)
    {
        if (ReferenceEquals(_current, window)) _current = null;
    }

    public static void Apply(IReadOnlyDictionary<string, object?> values)
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => Apply(values));
            return;
        }

        if (Application.Current?.Resources is { } resources)
        {
            var theme = values.TryGetValue("appearance.theme", out var setting) ? setting as string : null;
            var variant = theme switch
            {
                "Light" => MosaicThemeVariant.Light,
                "Dark" => MosaicThemeVariant.Dark,
                "HighContrast" => MosaicThemeVariant.HighContrast,
                _ => (MosaicThemeVariant?)null
            };

            if (variant is { } fixedVariant) MosaicThemeManager.Apply(resources, fixedVariant);
            else MosaicThemeManager.Attach(resources);

            var language = values.TryGetValue("ui.language", out var languageSetting) ? languageSetting as string : null;
            var resolvedLanguage = language switch
            {
                "Chinese" => MosaicLanguage.Chinese,
                "English" => MosaicLanguage.English,
                _ => MosaicLanguage.FollowSystem
            };
            MosaicLocalizationManager.SetLanguage(resolvedLanguage);
        }

        _current?.ApplyLayoutSettings(values);
    }
}
