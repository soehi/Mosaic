using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Presentation.Contracts;

namespace Mosaic.DesktopPresence.Wpf;

internal sealed class PresenceSettings(Action<IReadOnlyDictionary<string, object?>> apply, Func<IReadOnlyDictionary<string, object?>, double> maximumOffset, IUiDispatcher dispatcher) : ISettingSource
{
    public string ModuleId => "desktop-presence";
    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        Define("desktop.closeBehavior", "关闭主窗口", "驻留后台或退出应用；退出后边缘入口也会关闭", "Reside", ["Reside", "Exit"], normal: true),
        Define("desktop.edge.activation", "入口触发方式", "通过悬停、单击或双击打开任务摘要", "SingleClick", ["Hover", "SingleClick", "DoubleClick"], normal: true),
        Define("desktop.popup.filter", "摘要任务筛选", "弹窗只展示筛选后的任务快照", "All", ["All", "Open", "Completed"], normal: true),
        Define("desktop.popup.completionToggle", "显示完成按钮", "在速览中标记完成或恢复未完成", true, normal: true),
        Define("desktop.popup.filterSwitch", "显示筛选切换", "在当前速览内切换任务筛选", true, normal: true),
        Define("desktop.edge.monitor", "目标显示器", "主屏或主窗口上次所在的显示器", "Primary", ["Primary", "LastMainWindow"]),
        Define("desktop.edge.anchor", "边缘方向", "右侧靠上或顶部靠右", "RightEdgeTop", ["RightEdgeTop", "TopEdgeRight"]),
        Define("desktop.edge.tabSize", "入口尺寸", "方形入口的边长（DIP）", 32, min: 20, max: 64),
        Define("desktop.edge.visibleDepth", "入口可见深度", "从屏幕边缘露出的深度，不得超过入口尺寸", 12, min: 6, max: 64),
        Define("desktop.edge.offset", "边缘偏移", "入口距工作区上方或右侧的距离（DIP）", 24, min: 0),
        Define("desktop.edge.topmost", "入口置顶", "入口和弹窗显示在其他普通窗口上方", true),
        Define("desktop.edge.hoverDelayMilliseconds", "悬停展开延迟", "鼠标停留多久后展开（毫秒）", 300, min: 0, max: 2000),
        Define("desktop.popup.dismissDelayMilliseconds", "自动收起延迟", "离开入口和面板多久后收起（毫秒）", 250, min: 0, max: 2000),
        Define("desktop.popup.autoHide", "离开自动收起", "鼠标离开入口和面板后自动收起", true),
        Define("desktop.popup.width", "弹窗宽度", "任务摘要面板宽度（DIP）", 380, min: 280, max: 640),
        Define("desktop.popup.maxHeight", "弹窗最大高度", "面板高度同时受工作区限制（DIP）", 560, min: 240, max: 1000),
        Define("desktop.popup.maxItems", "摘要任务上限", "超出数量会明确提示，可恢复主窗口查看更多", 12, min: 1, max: 100),
        Define("desktop.popup.openColor", "未完成状态颜色", "使用 Theme 跟随主题，或输入 #RRGGBB", "Theme"),
        Define("desktop.popup.completedColor", "已完成状态颜色", "使用 Theme 跟随主题，或输入 #RRGGBB", "Theme"),
        Define("desktop.popup.fitContent", "按内容收缩", "少量任务时使用自然高度，仍受最大高度限制", true),
        Define("desktop.popup.padding", "面板内边距", "外框统一内边距（DIP）", 16, min: 8, max: 28),
        Define("desktop.popup.cornerRadius", "面板圆角", "任务速览外框圆角（DIP）", 12, min: 0, max: 24),
        Define("desktop.popup.rowGap", "任务行间距", "相邻任务行的额外间距（DIP）", 0, min: 0, max: 12),
        Define("desktop.popup.rowPaddingY", "任务行纵向留白", "每行上方和下方的留白（DIP）", 12, min: 6, max: 20),
        Define("desktop.popup.titleFontSize", "速览标题字号", "任务标题字号（DIP）", 14, min: 12, max: 20),
        Define("desktop.popup.titleMaxLines", "标题最多行数", "超出行数省略，悬停可查看完整标题", 2, min: 1, max: 3),
        Define("desktop.popup.showStatusText", "显示状态文字", "隐藏后仍保留矢量标记和无障碍名称", true),
        Define("desktop.popup.showHints", "常驻显示收起提示", "关闭时通过工具提示查看收起方式", false)
    ];

    private static SettingDefinition Define(string id, string name, string description, object value,
        string[]? options = null, int? min = null, int? max = null, bool normal = false) =>
        new(id, "desktop-presence", name, description,
            value is bool ? SettingValueKind.Boolean : value is int ? SettingValueKind.Integer : options is null ? SettingValueKind.String : SettingValueKind.Enum,
            value, min, max, options, SettingsGroup.DesktopPresence,
            normal ? SettingCategory.Normal : SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings,
            PreviewMode: SettingPreviewMode.Reversible,
            DisplayNameKey: DisplayNames[id],
            DescriptionKey: DisplayNames[id] + ".Description",
            Options: options?.Select(option => new SettingOption(option, PresenceValueKey(option))).ToArray());

    private static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["desktop.closeBehavior"] = "Settings.Setting.Desktop.CloseBehavior",
        ["desktop.edge.activation"] = "Settings.Setting.Desktop.EdgeActivation",
        ["desktop.popup.filter"] = "Settings.Setting.Desktop.PopupFilter",
        ["desktop.popup.completionToggle"] = "Settings.Setting.Desktop.PopupCompletionToggle",
        ["desktop.popup.filterSwitch"] = "Settings.Setting.Desktop.PopupFilterSwitch",
        ["desktop.edge.monitor"] = "Settings.Setting.Desktop.EdgeMonitor",
        ["desktop.edge.anchor"] = "Settings.Setting.Desktop.EdgeAnchor",
        ["desktop.edge.tabSize"] = "Settings.Setting.Desktop.EdgeTabSize",
        ["desktop.edge.visibleDepth"] = "Settings.Setting.Desktop.EdgeVisibleDepth",
        ["desktop.edge.offset"] = "Settings.Setting.Desktop.EdgeOffset",
        ["desktop.edge.topmost"] = "Settings.Setting.Desktop.EdgeTopmost",
        ["desktop.edge.hoverDelayMilliseconds"] = "Settings.Setting.Desktop.HoverDelay",
        ["desktop.popup.dismissDelayMilliseconds"] = "Settings.Setting.Desktop.DismissDelay",
        ["desktop.popup.autoHide"] = "Settings.Setting.Desktop.AutoHide",
        ["desktop.popup.width"] = "Settings.Setting.Desktop.PopupWidth",
        ["desktop.popup.maxHeight"] = "Settings.Setting.Desktop.PopupMaxHeight",
        ["desktop.popup.maxItems"] = "Settings.Setting.Desktop.PopupMaxItems",
        ["desktop.popup.openColor"] = "Settings.Setting.Desktop.OpenColor",
        ["desktop.popup.completedColor"] = "Settings.Setting.Desktop.CompletedColor",
        ["desktop.popup.fitContent"] = "Settings.Setting.Desktop.FitContent",
        ["desktop.popup.padding"] = "Settings.Setting.Desktop.PopupPadding",
        ["desktop.popup.cornerRadius"] = "Settings.Setting.Desktop.PopupCornerRadius",
        ["desktop.popup.rowGap"] = "Settings.Setting.Desktop.PopupRowGap",
        ["desktop.popup.rowPaddingY"] = "Settings.Setting.Desktop.PopupRowPaddingY",
        ["desktop.popup.titleFontSize"] = "Settings.Setting.Desktop.PopupTitleFontSize",
        ["desktop.popup.titleMaxLines"] = "Settings.Setting.Desktop.PopupTitleMaxLines",
        ["desktop.popup.showStatusText"] = "Settings.Setting.Desktop.PopupShowStatusText",
        ["desktop.popup.showHints"] = "Settings.Setting.Desktop.PopupShowHints"
    };

    private static string PresenceValueKey(string value) => value switch
    {
        "LastMainWindow" => "Setting.Value.LastMainWindow",
        "RightEdgeTop" => "Setting.Value.RightEdgeTop",
        "TopEdgeRight" => "Setting.Value.TopEdgeRight",
        _ => "Setting.Value." + value
    };

    public async Task<OperationResult> ValidateAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        OperationResult Error(string id, string message) => OperationResult.Failure(new(ErrorCodes.ValidationFailed, message, id));
        if (ReadInt(values, "desktop.edge.visibleDepth", 12) > ReadInt(values, "desktop.edge.tabSize", 32))
            return Error("desktop.edge.visibleDepth", "可见深度不得大于入口尺寸。");
        var limit = 0d;
        await dispatcher.InvokeAsync(() => limit = maximumOffset(values));
        if (ReadInt(values, "desktop.edge.offset", 24) > limit)
            return Error("desktop.edge.offset", "偏移不得超出目标工作区。");
        foreach (var id in new[] { "desktop.popup.openColor", "desktop.popup.completedColor" })
        {
            var color = ReadString(values, id, "Theme");
            if (color != "Theme" && (color.Length != 7 || color[0] != '#' || !color[1..].All(Uri.IsHexDigit)))
                return Error(id, "颜色仅接受 Theme 或六位 #RRGGBB。");
        }
        return OperationResult.Success();
    }
    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return dispatcher.InvokeAsync(() => apply(values));
    }
    internal static int ReadInt(IReadOnlyDictionary<string, object?> values, string id, int fallback) =>
        values.TryGetValue(id, out var value) && value is not null ? Convert.ToInt32(value) : fallback;
    internal static string ReadString(IReadOnlyDictionary<string, object?> values, string id, string fallback) =>
        values.TryGetValue(id, out var value) && value is string text ? text : fallback;
    internal static bool ReadBool(IReadOnlyDictionary<string, object?> values, string id, bool fallback) =>
        values.TryGetValue(id, out var value) && value is bool boolean ? boolean : fallback;
}
