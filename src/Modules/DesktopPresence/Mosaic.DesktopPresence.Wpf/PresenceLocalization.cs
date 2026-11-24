using System.Windows;
using Mosaic.Presentation.Wpf;

namespace Mosaic.DesktopPresence.Wpf;

internal static class PresenceLocalization
{
    internal static ResourceDictionary Dictionary(MosaicLanguage language)
    {
        var en = language == MosaicLanguage.English;
        var resources = new ResourceDictionary();
        void Add(string key, string zh, string english) => resources[key] = en ? english : zh;
        Add("Presence.Edge", "Mosaic 边缘入口", "Mosaic edge entry");
        Add("Presence.Title", "任务速览", "Task overview");
        Add("Presence.Subtitle", "看一眼进度，再继续手边的事。", "A quick look at your progress.");
        Add("Presence.Dismiss", "收起速览（Esc）", "Dismiss overview (Esc)");
        Add("Presence.AutoHideHint", "离开面板后自动收起 · Esc 收起", "Auto-hides when you leave · Esc to dismiss");
        Add("Presence.ManualHideHint", "按 Esc 或点击右上角收起", "Press Esc or use the close button to dismiss");
        Add("Presence.Restore", "打开主窗口", "Open main window");
        Add("Presence.Exit", "退出 Mosaic", "Exit Mosaic");
        Add("Presence.ExitShort", "退出", "Exit");
        Add("Presence.More", "更多操作", "More actions");
        Add("Presence.Failed", "退出失败，请重试或打开主窗口。", "Could not exit. Try again or open the main window.");
        Add("Settings.Section.DesktopPresence", "驻留与边缘", "Residence & edge");
        var names = new (string Id, string Zh, string En, string ZhDescription, string EnDescription)[]
        {
            ("desktop.closeBehavior", "关闭主窗口", "Closing the main window", "驻留后台保留草稿；选择退出将结束进程", "Keep drafts in the background, or exit the application"),
            ("desktop.edge.showTiming", "任务速览入口显示时机", "Entry visibility timing", "主窗口关闭后出现；也可选择最小化时出现或一直显示", "Show after the main window is hidden; optionally when minimized or always"),
            ("desktop.edge.activation", "入口触发方式", "Edge activation", "通过悬停、单击或双击打开任务速览", "Open the overview by hovering, clicking or double-clicking"),
            ("desktop.popup.filter", "速览默认筛选", "Default overview filter", "在速览中选择筛选前或隐藏筛选切换时使用", "Used until you choose a filter in the overview or when the filter switch is hidden"),
            ("desktop.edge.monitor", "目标显示器", "Target monitor", "使用主显示器或主窗口上次所在的显示器", "Use the primary monitor or the last main-window monitor"),
            ("desktop.edge.anchor", "边缘方向", "Edge position", "右边缘靠上或上边缘靠右", "Near the top of the right edge or right of the top edge"),
            ("desktop.edge.tabSize", "入口尺寸", "Entry size", "方形入口的边长（DIP）", "Square edge-entry size in DIP"),
            ("desktop.edge.visibleDepth", "入口可见深度", "Visible depth", "露出深度不得超过入口尺寸", "Visible depth must not exceed the entry size"),
            ("desktop.edge.offset", "边缘偏移", "Edge offset", "距工作区上方或右侧的距离（DIP）", "Distance from the top or right of the work area in DIP"),
            ("desktop.edge.topmost", "入口置顶", "Keep on top", "入口和弹窗显示在其他普通窗口上方", "Keep the entry and popup above normal windows"),
            ("desktop.edge.hoverDelayMilliseconds", "悬停展开延迟", "Hover delay", "鼠标停留多久后展开（毫秒）", "Delay before opening on hover in milliseconds"),
            ("desktop.popup.dismissDelayMilliseconds", "自动收起延迟", "Dismiss delay", "离开入口和面板多久后收起（毫秒）", "Delay after leaving the entry and popup in milliseconds"),
            ("desktop.popup.autoHide", "离开自动收起", "Auto-hide on leave", "鼠标离开入口和面板后自动收起", "Dismiss after the pointer leaves both entry and popup"),
            ("desktop.popup.width", "弹窗宽度", "Popup width", "任务速览面板宽度（DIP）", "Overview width in DIP"),
            ("desktop.popup.maxHeight", "弹窗最大高度", "Maximum popup height", "实际高度同时受工作区限制（DIP）", "Height is also clamped to the work area in DIP"),
            ("desktop.popup.maxItems", "摘要任务上限", "Maximum overview items", "超出数量会提示，可打开主窗口查看更多", "Show an overflow message and open the main window for more"),
            ("desktop.popup.openColor", "未完成状态颜色", "Open status color", "Theme 跟随主题，或输入 #RRGGBB；高对比度优先", "Use Theme or #RRGGBB; high contrast takes priority"),
            ("desktop.popup.completedColor", "已完成状态颜色", "Completed status color", "Theme 跟随主题，或输入 #RRGGBB；高对比度优先", "Use Theme or #RRGGBB; high contrast takes priority")
        };
        foreach (var item in names)
        {
            Add("Settings.Setting." + item.Id, item.Zh, item.En);
            Add("Settings.Setting." + item.Id + ".Description", item.ZhDescription, item.EnDescription);
        }
        foreach (var (key, zh, english, description, enDescription) in new[]
        {
            ("PopupCompletionToggle", "显示完成按钮", "Show completion button", "在速览中标记完成或恢复未完成", "Complete or reopen tasks in the overview"),
            ("PopupFilterSwitch", "显示筛选切换", "Show filter switch", "在速览中切换筛选，下次打开时恢复上次选择", "Choose a filter in the overview and resume it next time"),
            ("FitContent", "按内容收缩", "Fit popup to content", "少量任务使用自然高度，仍受最大高度限制", "Use natural height for short lists, within the maximum height"),
            ("PopupPadding", "面板内边距", "Popup padding", "外框统一内边距（DIP）", "Single outer content padding in DIP"),
            ("PopupCornerRadius", "面板圆角", "Popup corner radius", "任务速览外框圆角（DIP）", "Overview frame corner radius in DIP"),
            ("PopupRowGap", "任务行间距", "Row gap", "相邻任务行的额外间距（DIP）", "Additional spacing between task rows in DIP"),
            ("PopupRowPaddingY", "任务行纵向留白", "Row vertical padding", "每行上方和下方的留白（DIP）", "Space above and below each row in DIP"),
            ("PopupTitleFontSize", "速览标题字号", "Task title size", "任务标题字号（DIP）", "Task title font size in DIP"),
            ("PopupTitleMaxLines", "标题最多行数", "Maximum title lines", "超出行数省略，悬停可查看完整标题", "Truncate longer titles; hover to read the full title"),
            ("PopupShowHints", "常驻显示收起提示", "Show dismissal hint", "关闭时通过工具提示查看收起方式", "When hidden, dismissal instructions remain in a tooltip")
        })
        {
            Add("Settings.Setting.Desktop." + key, zh, english);
            Add("Settings.Setting.Desktop." + key + ".Description", description, enDescription);
        }
        foreach (var (value, zh, english) in new[]
        {
            ("Reside", "驻留后台", "Keep in background"), ("Exit", "退出应用", "Exit application"),
            ("Resident", "主窗口关闭后", "After main window hides"), ("Minimized", "主窗口最小化后", "After main window minimizes"), ("Always", "一直显示", "Always"),
            ("Hover", "鼠标悬停", "Hover"), ("SingleClick", "单击", "Single click"), ("DoubleClick", "双击", "Double click"),
            ("Primary", "主显示器", "Primary monitor"), ("LastMainWindow", "主窗口所在显示器", "Last main-window monitor"),
            ("RightEdgeTop", "右边缘靠上", "Right edge, near top"), ("TopEdgeRight", "上边缘靠右", "Top edge, near right"),
            ("All", "全部任务", "All tasks"), ("Open", "未完成", "Open"), ("Completed", "已完成", "Completed")
        }) Add("Setting.Value." + value, zh, english);
        return resources;
    }
}
