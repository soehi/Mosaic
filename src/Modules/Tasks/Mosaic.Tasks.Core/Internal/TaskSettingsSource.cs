using Mosaic.Settings.Contracts;

namespace Mosaic.Tasks.Core.Internal;

internal sealed class TaskSettingsSource(TaskRules rules) : ISettingSource
{
    public string ModuleId => "tasks-core";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new("tasks.sortOrder", "tasks-core", "任务排序", "任务列表默认排序方式", SettingValueKind.Enum, "CreatedDesc", null, null, ["CreatedDesc", "CreatedAsc", "TitleAsc", "TitleDesc"], SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.title.maxCharacters", "tasks-core", "标题上限", "任务标题允许的最大字符数", SettingValueKind.Integer, 200, 1, 1000, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "收紧后旧任务仍可读，编辑超限需截断"),
        new("tasks.notes.maxCharacters", "tasks-core", "备注上限", "任务备注允许的最大字符数", SettingValueKind.Integer, 20000, 1000, 200000, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "过大会增加编辑与保存负担"),
        new("tasks.title.required", "tasks-core", "标题必填", "新建任务是否必须填写标题", SettingValueKind.Boolean, true, null, null, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "关闭后允许空标题"),
        new("tasks.defaultCompleted", "tasks-core", "新任务默认完成", "新建任务是否直接标记完成", SettingValueKind.Boolean, false, null, null, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "新任务可能不在“未完成”筛选出现"),
        new("tasks.revisionStart", "tasks-core", "版本起点", "新建任务的初始版本号", SettingValueKind.Integer, 1, 1, 1000000, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "仅影响新任务初始版本")
    ];

    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        rules.Apply(values);
        return Task.CompletedTask;
    }
}
