using Mosaic.Presentation.Contracts;
using Mosaic.Settings.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskUiSettingsTarget
{
    private Action<IReadOnlyDictionary<string, object?>>? _handler;

    public void Attach(Action<IReadOnlyDictionary<string, object?>> handler) => _handler = handler;

    public void Detach() => _handler = null;

    public void Apply(IReadOnlyDictionary<string, object?> values) => _handler?.Invoke(values);
}

internal sealed class TaskUiSettingSource(IUiDispatcher dispatcher, TaskUiSettingsTarget target) : ISettingSource
{
    public string ModuleId => "tasks-ui";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new("tasks.confirmDelete", "tasks-ui", "删除前确认", "删除任务前显示确认框", SettingValueKind.Boolean, true, null, null, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.confirmUnsaved", "tasks-ui", "未保存离开确认", "离开编辑器前确认未保存内容", SettingValueKind.Boolean, true, null, null, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.defaultFilter", "tasks-ui", "默认筛选", "打开任务页时使用的筛选", SettingValueKind.Enum, "All", null, null, ["All", "Open", "Completed"], SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.showCharacterCount", "tasks-ui", "显示字符计数", "在任务编辑器中显示字符数", SettingValueKind.Boolean, true, null, null, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.autoSwitchToAllOnHidden", "tasks-ui", "隐藏任务自动切回全部", "当前筛选下任务不可见时自动切回全部", SettingValueKind.Boolean, true, null, null, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.layout.compactBreakpoint", "tasks-ui", "任务窄屏断点", "低于该宽度时压缩间距并允许页头动作换行，保持左右双栏", SettingValueKind.Integer, 920, 600, 1400, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.layout.editorPaneWidth", "tasks-ui", "编辑栏宽度", "右侧新建与编辑栏的首选宽度，空间不足时最低保留 360 DIP", SettingValueKind.Integer, 480, 360, 720, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.layout.paneGap", "tasks-ui", "双栏间距", "任务列表与右侧编辑栏之间的间距", SettingValueKind.Integer, 24, 0, 64, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.editor.compactActionsWidth", "tasks-ui", "操作栏窄宽断点", "低于该宽度时操作栏换行", SettingValueKind.Integer, 480, 360, 900, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.editor.compactActionsHeight", "tasks-ui", "操作栏矮窗断点", "低于该高度时操作栏换行", SettingValueKind.Integer, 680, 480, 1000, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.list.minColumnWidth", "tasks-ui", "列表最小列宽", "并排布局中任务列表最小宽度", SettingValueKind.Integer, 300, 220, 500, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.editor.notesMinHeight", "tasks-ui", "备注最小高度", "任务备注输入框最小高度", SettingValueKind.Integer, 128, 96, 360, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.button.minWidth", "tasks-ui", "按钮最小宽度", "任务操作按钮最小宽度", SettingValueKind.Integer, 96, 72, 160, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.button.minHeight", "tasks-ui", "按钮最小高度", "任务操作按钮最小高度", SettingValueKind.Integer, 40, 32, 56, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("ui.text.emptyAllTitle", "tasks-ui", "空列表标题（全部）", "全部筛选下空列表标题", SettingValueKind.String, "这里还没有任务", null, null, null, SettingsGroup.Text, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("ui.text.emptyAllDescription", "tasks-ui", "空列表描述（全部）", "全部筛选下空列表描述", SettingValueKind.String, "新建一项任务，给今天一个开始。", null, null, null, SettingsGroup.Text, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("ui.text.emptyOpenTitle", "tasks-ui", "空列表标题（未完成）", "未完成筛选下空列表标题", SettingValueKind.String, "没有未完成任务", null, null, null, SettingsGroup.Text, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("ui.text.emptyOpenDescription", "tasks-ui", "空列表描述（未完成）", "未完成筛选下空列表描述", SettingValueKind.String, "新建任务或恢复一项任务后，会显示在这里。", null, null, null, SettingsGroup.Text, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("ui.text.emptyCompletedTitle", "tasks-ui", "空列表标题（已完成）", "已完成筛选下空列表标题", SettingValueKind.String, "还没有已完成的任务", null, null, null, SettingsGroup.Text, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("ui.text.emptyCompletedDescription", "tasks-ui", "空列表描述（已完成）", "已完成筛选下空列表描述", SettingValueKind.String, "完成一项任务后，会显示在这里。", null, null, null, SettingsGroup.Text, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("ui.text.untitledTask", "tasks-ui", "未命名任务文案", "无标题任务在列表中的显示文案", SettingValueKind.String, "未命名任务", null, null, null, SettingsGroup.Text, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("ui.text.saveSuccess", "tasks-ui", "保存成功文案", "保存任务后的提示文案", SettingValueKind.String, "已保存到此电脑", null, null, null, SettingsGroup.Text, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("ui.text.deleteSuccess", "tasks-ui", "删除成功文案", "删除任务后的提示文案", SettingValueKind.String, "任务已删除", null, null, null, SettingsGroup.Text, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("ui.text.completedSuccess", "tasks-ui", "完成提示文案", "标记完成后的提示文案", SettingValueKind.String, "已标记完成", null, null, null, SettingsGroup.Text, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("ui.text.restoredSuccess", "tasks-ui", "恢复提示文案", "恢复未完成后的提示文案", SettingValueKind.String, "已恢复为未完成", null, null, null, SettingsGroup.Text, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings)
    ];

    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        dispatcher.CheckAccess() ? ApplyCore(values) : dispatcher.InvokeAsync(() => ApplyCore(values));

    private Task ApplyCore(IReadOnlyDictionary<string, object?> values)
    {
        target.Apply(values);
        return Task.CompletedTask;
    }
}
