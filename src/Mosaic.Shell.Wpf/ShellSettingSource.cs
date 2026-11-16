using System.Windows;
using System.Windows.Threading;
using Mosaic.Presentation.Wpf;
using Mosaic.Settings.Contracts;

namespace Mosaic.Shell.Wpf;

public sealed class ShellSettingSource : ISettingSource
{
    public string ModuleId => "shell";

    public IReadOnlyList<SettingDefinition> Definitions { get; } = Localize(
    [
        new("appearance.theme", "shell", "主题", "界面配色跟随系统或固定主题", SettingValueKind.Enum, "FollowSystem", null, null, ["FollowSystem", "Light", "Dark", "HighContrast"], SettingsGroup.Appearance, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible),
        new("ui.language", "shell", "界面语言", "选择中文或英文界面", SettingValueKind.Enum, "FollowSystem", null, null, ["FollowSystem", "Chinese", "English"], SettingsGroup.Appearance, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible),
        new("shell.sidebar.collapseBelowWidth", "shell", "侧栏折叠宽度", "窗口小于该宽度时折叠侧栏", SettingValueKind.Integer, 1180, 900, 1600, null, SettingsGroup.Appearance, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible),
        new("shell.sidebar.expandAtWidth", "shell", "侧栏展开宽度", "窗口达到该宽度时重新展开侧栏，建议大于折叠宽度 20 DIP", SettingValueKind.Integer, 1200, 900, 1800, null, SettingsGroup.Appearance, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible),
        new("shell.sidebar.fullWidth", "shell", "完整侧栏宽度", "展开状态的侧栏宽度", SettingValueKind.Integer, 190, 160, 260, null, SettingsGroup.Appearance, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible),
        new("shell.sidebar.railWidth", "shell", "图标栏宽度", "折叠状态的侧栏宽度", SettingValueKind.Integer, 64, 48, 88, null, SettingsGroup.Appearance, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible),
        new("shell.page.marginLeft", "shell", "页面左边距", "任务等内容页左侧边距", SettingValueKind.Integer, 32, 8, 64, null, SettingsGroup.Appearance, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible),
        new("shell.page.marginTop", "shell", "页面上边距", "任务等内容页顶部边距", SettingValueKind.Integer, 30, 8, 64, null, SettingsGroup.Appearance, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible),
        new("shell.page.marginRight", "shell", "页面右边距", "任务等内容页右侧边距", SettingValueKind.Integer, 28, 8, 64, null, SettingsGroup.Appearance, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible),
        new("shell.page.marginBottom", "shell", "页面下边距", "任务等内容页底部边距", SettingValueKind.Integer, 28, 8, 64, null, SettingsGroup.Appearance, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible)
    ]);

    private static IReadOnlyList<SettingDefinition> Localize(IEnumerable<SettingDefinition> definitions)
    {
        return definitions.Select(definition => definition with
        {
            DisplayNameKey = DisplayNames.GetValueOrDefault(definition.Id, "Settings.Setting." + definition.Id),
            DescriptionKey = Descriptions.GetValueOrDefault(definition.Id, "Settings.Setting." + definition.Id + ".Description"),
            Options = definition.Kind == SettingValueKind.Enum ? OptionsFor(definition.Id) : null
        }).ToArray();
    }

    private static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["appearance.theme"] = "Settings.Setting.Appearance.Theme",
        ["ui.language"] = "Settings.Setting.Language",
        ["shell.sidebar.collapseBelowWidth"] = "Settings.Setting.SidebarCollapseWidth",
        ["shell.sidebar.expandAtWidth"] = "Settings.Setting.SidebarExpandWidth",
        ["shell.sidebar.fullWidth"] = "Settings.Setting.SidebarFullWidth",
        ["shell.sidebar.railWidth"] = "Settings.Setting.SidebarRailWidth",
        ["shell.page.marginLeft"] = "Settings.Setting.PageMarginLeft",
        ["shell.page.marginTop"] = "Settings.Setting.PageMarginTop",
        ["shell.page.marginRight"] = "Settings.Setting.PageMarginRight",
        ["shell.page.marginBottom"] = "Settings.Setting.PageMarginBottom"
    };

    private static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["appearance.theme"] = "Settings.Setting.Appearance.Theme.Description",
        ["ui.language"] = "Settings.Setting.Language.Description",
        ["shell.sidebar.collapseBelowWidth"] = "Settings.Setting.SidebarCollapseWidth.Description",
        ["shell.sidebar.expandAtWidth"] = "Settings.Setting.SidebarExpandWidth.Description",
        ["shell.sidebar.fullWidth"] = "Settings.Setting.SidebarFullWidth.Description",
        ["shell.sidebar.railWidth"] = "Settings.Setting.SidebarRailWidth.Description",
        ["shell.page.marginLeft"] = "Settings.Setting.PageMarginLeft.Description",
        ["shell.page.marginTop"] = "Settings.Setting.PageMarginTop.Description",
        ["shell.page.marginRight"] = "Settings.Setting.PageMarginRight.Description",
        ["shell.page.marginBottom"] = "Settings.Setting.PageMarginBottom.Description"
    };

    private static IReadOnlyList<SettingOption> OptionsFor(string id) => id switch
    {
        "appearance.theme" =>
        [
            new("FollowSystem", "Setting.Value.FollowSystem"),
            new("Light", "Setting.Value.Light"),
            new("Dark", "Setting.Value.Dark"),
            new("HighContrast", "Setting.Value.HighContrast")
        ],
        "ui.language" =>
        [
            new("FollowSystem", "Setting.Value.FollowSystemLanguage"),
            new("Chinese", "Setting.Value.Chinese"),
            new("English", "Setting.Value.English")
        ],
        _ => []
    };

    public Task<Mosaic.Contracts.OperationResult> ValidateAsync(IReadOnlyDictionary<string, object?> effectiveValues, CancellationToken cancellationToken = default)
    {
        if (effectiveValues.TryGetValue("shell.sidebar.collapseBelowWidth", out var collapse) &&
            effectiveValues.TryGetValue("shell.sidebar.expandAtWidth", out var expand) &&
            Convert.ToDouble(expand) < Convert.ToDouble(collapse) + 20)
            return Task.FromResult(Mosaic.Contracts.OperationResult.Failure(new(
                Mosaic.Contracts.ErrorCodes.ValidationFailed, "展开宽度至少比折叠宽度大 20 DIP。", "shell.sidebar.expandAtWidth")));
        return Task.FromResult(Mosaic.Contracts.OperationResult.Success());
    }

    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ShellSettingsApplier.ApplyAsync(values);
    }
}

internal static class ShellSettingsApplier
{
    private static ShellWindow? _current;
    private static IReadOnlyDictionary<string, object?>? _latest;

    internal static void Attach(ShellWindow window)
    {
        _current = window;
        if (_latest is not null && Application.Current is not null) window.ApplyLayoutSettings(_latest);
    }

    internal static void Detach(ShellWindow window)
    {
        if (ReferenceEquals(_current, window)) _current = null;
    }

    public static Task ApplyAsync(IReadOnlyDictionary<string, object?> values)
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(() => ApplyCore(values)).Task;
        }
        ApplyCore(values);
        return Task.CompletedTask;
    }
    private static void ApplyCore(IReadOnlyDictionary<string, object?> values)
    {
        _latest = Application.Current is null ? null : new Dictionary<string, object?>(values);

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
