using Mosaic.Settings.Contracts;

namespace Mosaic.Tasks.Core.Internal;

internal sealed class TaskSettingsSource(TaskRules rules, TaskAutoArchive archive) : ISettingSource
{
    public string ModuleId => "tasks-core";

    public IReadOnlyList<SettingDefinition> Definitions { get; } = Localize(
    [
        new("tasks.archive.autoEnabled", "tasks-core", "自动归档", "仅自动归档等待期已满且未恢复的已完成任务；未完成任务永远不会被自动归档；关闭时不自动归档任何任务", SettingValueKind.Boolean, false, null, null, null, SettingsGroup.Tasks, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.None),
        new("tasks.archive.delayDays", "tasks-core", "归档等待天数", "从完成或恢复到自动归档的天数", SettingValueKind.Integer, 7, 1, 365, null, SettingsGroup.Tasks, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.None),
        new("tasks.sortOrder", "tasks-core", "任务排序", "任务列表默认排序方式", SettingValueKind.Enum, "CreatedDesc", null, null, ["CreatedDesc", "CreatedAsc", "TitleAsc", "TitleDesc"], SettingsGroup.Tasks, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible),
        new("tasks.title.maxCharacters", "tasks-core", "标题上限", "任务标题允许的最大字符数", SettingValueKind.Integer, 200, 1, 1000, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "收紧后旧任务仍可读，编辑超限需截断"),
        new("tasks.notes.maxCharacters", "tasks-core", "备注上限", "任务备注允许的最大字符数", SettingValueKind.Integer, 20000, 1000, 200000, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "过大会增加编辑与保存负担"),
        new("tasks.title.required", "tasks-core", "标题必填", "新建任务是否必须填写标题", SettingValueKind.Boolean, true, null, null, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "关闭后允许空标题"),
        new("tasks.defaultCompleted", "tasks-core", "新任务默认完成", "新建任务是否直接标记完成", SettingValueKind.Boolean, false, null, null, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "新任务可能不在“未完成”筛选出现"),
        new("tasks.revisionStart", "tasks-core", "版本起点", "新建任务的初始版本号", SettingValueKind.Integer, 1, 1, 1000000, null, SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "仅影响新任务初始版本")
    ]);

    private static IReadOnlyList<SettingDefinition> Localize(IEnumerable<SettingDefinition> definitions) =>
        definitions.Select(definition => definition with
        {
            DisplayNameKey = DisplayNames[definition.Id],
            DescriptionKey = DisplayNames[definition.Id] + ".Description",
            Options = definition.Id == "tasks.sortOrder"
                ? AllowedOptions(["CreatedDesc", "CreatedAsc", "TitleAsc", "TitleDesc"])
                : null
        }).ToArray();

    private static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tasks.archive.autoEnabled"] = "Settings.Setting.AutoArchive",
        ["tasks.archive.delayDays"] = "Settings.Setting.ArchiveDelay",
        ["tasks.sortOrder"] = "Settings.Setting.SortOrder",
        ["tasks.title.maxCharacters"] = "Settings.Setting.TitleMax",
        ["tasks.notes.maxCharacters"] = "Settings.Setting.NotesMax",
        ["tasks.title.required"] = "Settings.Setting.TitleRequired",
        ["tasks.defaultCompleted"] = "Settings.Setting.DefaultCompleted",
        ["tasks.revisionStart"] = "Settings.Setting.RevisionStart"
    };

    private static IReadOnlyList<SettingOption> AllowedOptions(IEnumerable<string> values) =>
        values.Select(value => new SettingOption(value, "Setting.Value." + value)).ToArray();

    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        rules.Apply(values);
        return archive.ApplyAsync(values);
    }
}
