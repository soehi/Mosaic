using Mosaic.Settings.Contracts;

namespace Mosaic.Settings.Core;

public static class SettingCatalog
{
    private static IReadOnlyList<SettingDefinition>? _definitions;

    public static IReadOnlyList<SettingDefinition> Definitions => _definitions ??= Create();

    public static IReadOnlyList<SettingDefinition> ForModule(string moduleId) =>
        Definitions.Where(item => item.ModuleId == moduleId).ToArray();

    public static IReadOnlyList<SettingOption> OptionsFor(string settingId) => settingId switch
    {
        "appearance.theme" => Options("Setting.Value.FollowSystem", "Setting.Value.Light", "Setting.Value.Dark", "Setting.Value.HighContrast"),
        "ui.language" => Options("Setting.Value.FollowSystemLanguage", "Setting.Value.Chinese", "Setting.Value.English"),
        "tasks.defaultFilter" => Options("Setting.Value.All", "Setting.Value.Open", "Setting.Value.Completed"),
        "tasks.sortOrder" => Options("Setting.Value.CreatedDesc", "Setting.Value.CreatedAsc", "Setting.Value.TitleAsc", "Setting.Value.TitleDesc"),
        "storage.journalMode" => Options("Setting.Value.Delete", "Setting.Value.Wal"),
        "storage.connectionMode" => Options("Setting.Value.ReusedSingle", "Setting.Value.PooledPerOperation"),
        "logging.level" => Options("Setting.Value.Debug", "Setting.Value.Information", "Setting.Value.Warning", "Setting.Value.Error", "Setting.Value.None"),
        _ => []
    };

    private static IReadOnlyList<SettingOption> Options(params string[] displayNameKeys) => displayNameKeys
        .Select((displayNameKey, index) => new SettingOption(ValuesFor(displayNameKey)[index], displayNameKey))
        .ToArray();

    private static string[] ValuesFor(string displayNameKey) => displayNameKey switch
    {
        "Setting.Value.FollowSystem" => ["FollowSystem"],
        "Setting.Value.Light" => ["Light"],
        "Setting.Value.Dark" => ["Dark"],
        "Setting.Value.HighContrast" => ["HighContrast"],
        "Setting.Value.FollowSystemLanguage" => ["FollowSystem"],
        "Setting.Value.Chinese" => ["Chinese"],
        "Setting.Value.English" => ["English"],
        "Setting.Value.All" => ["All"],
        "Setting.Value.Open" => ["Open"],
        "Setting.Value.Completed" => ["Completed"],
        "Setting.Value.CreatedDesc" => ["CreatedDesc"],
        "Setting.Value.CreatedAsc" => ["CreatedAsc"],
        "Setting.Value.TitleAsc" => ["TitleAsc"],
        "Setting.Value.TitleDesc" => ["TitleDesc"],
        "Setting.Value.Delete" => ["Delete"],
        "Setting.Value.Wal" => ["Wal"],
        "Setting.Value.ReusedSingle" => ["ReusedSingle"],
        "Setting.Value.PooledPerOperation" => ["PooledPerOperation"],
        "Setting.Value.Debug" => ["Debug"],
        "Setting.Value.Information" => ["Information"],
        "Setting.Value.Warning" => ["Warning"],
        "Setting.Value.Error" => ["Error"],
        _ => ["None"]
    };

    private static IReadOnlyList<SettingDefinition> Create()
    {
        const string shell = "shell";
        const string tasksUi = "tasks-ui";
        const string tasksCore = "tasks-core";
        const string storage = "storage-sqlite";
        const string host = "host";
        const string text = "tasks-ui";

        return
        [
            Definition("appearance.theme", shell, "主题", "界面配色跟随系统或固定主题", SettingValueKind.Enum, "FollowSystem", null, null, ["FollowSystem", "Light", "Dark", "HighContrast"], SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("ui.language", shell, "界面语言", "选择中文或英文界面", SettingValueKind.Enum, "FollowSystem", null, null, ["FollowSystem", "Chinese", "English"], SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("tasks.confirmDelete", tasksUi, "删除前确认", "删除任务前显示确认框", SettingValueKind.Boolean, true, null, null, null, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("tasks.confirmUnsaved", tasksUi, "未保存离开确认", "离开编辑器前确认未保存内容", SettingValueKind.Boolean, true, null, null, null, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("tasks.defaultFilter", tasksUi, "默认筛选", "打开任务页时使用的筛选", SettingValueKind.Enum, "All", null, null, ["All", "Open", "Completed"], SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("tasks.showCharacterCount", tasksUi, "显示字符计数", "在任务编辑器中显示字符数", SettingValueKind.Boolean, true, null, null, null, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("tasks.autoSwitchToAllOnHidden", tasksUi, "隐藏任务自动切回全部", "当前筛选下任务不可见时自动切回全部", SettingValueKind.Boolean, true, null, null, null, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("tasks.sortOrder", tasksCore, "任务排序", "任务列表默认排序方式", SettingValueKind.Enum, "CreatedDesc", null, null, ["CreatedDesc", "CreatedAsc", "TitleAsc", "TitleDesc"], SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings),

            Definition("tasks.title.maxCharacters", tasksCore, "标题上限", "任务标题允许的最大字符数", SettingValueKind.Integer, 200, 1, 1000, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "收紧后旧任务仍可读，编辑超限需截断"),
            Definition("tasks.notes.maxCharacters", tasksCore, "备注上限", "任务备注允许的最大字符数", SettingValueKind.Integer, 20000, 1000, 200000, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "过大会增加编辑与保存负担"),
            Definition("tasks.title.required", tasksCore, "标题必填", "新建任务是否必须填写标题", SettingValueKind.Boolean, true, null, null, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "关闭后允许空标题"),
            Definition("tasks.defaultCompleted", tasksCore, "新任务默认完成", "新建任务是否直接标记完成", SettingValueKind.Boolean, false, null, null, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "新任务可能不在“未完成”筛选出现"),
            Definition("tasks.revisionStart", tasksCore, "版本起点", "新建任务的初始版本号", SettingValueKind.Integer, 1, 1, 1000000, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "仅影响新任务初始版本"),

            Definition("shell.sidebar.collapseBelowWidth", shell, "侧栏折叠宽度", "窗口小于该宽度时折叠侧栏", SettingValueKind.Integer, 1040, 720, 1600, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("shell.sidebar.expandAtWidth", shell, "侧栏展开宽度", "窗口达到该宽度时重新展开", SettingValueKind.Integer, 1060, 740, 1800, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("shell.sidebar.fullWidth", shell, "完整侧栏宽度", "展开状态的侧栏宽度", SettingValueKind.Integer, 190, 160, 260, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("shell.sidebar.railWidth", shell, "图标栏宽度", "折叠状态的侧栏宽度", SettingValueKind.Integer, 64, 48, 88, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("shell.window.minWidth", host, "窗口最小宽度", "允许的最小窗口宽度", SettingValueKind.Integer, 760, 640, 1200, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
            Definition("shell.window.minHeight", host, "窗口最小高度", "允许的最小窗口高度", SettingValueKind.Integer, 620, 520, 900, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
            Definition("shell.page.marginLeft", shell, "页面左边距", "任务等内容页左侧边距", SettingValueKind.Integer, 32, 8, 64, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("shell.page.marginTop", shell, "页面上边距", "任务等内容页顶部边距", SettingValueKind.Integer, 30, 8, 64, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("shell.page.marginRight", shell, "页面右边距", "任务等内容页右侧边距", SettingValueKind.Integer, 28, 8, 64, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("shell.page.marginBottom", shell, "页面下边距", "任务等内容页底部边距", SettingValueKind.Integer, 28, 8, 64, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("tasks.layout.compactBreakpoint", tasksUi, "任务窄屏断点", "低于该宽度时列表与详情单页切换", SettingValueKind.Integer, 920, 600, 1400, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("tasks.editor.compactActionsWidth", tasksUi, "操作栏窄宽断点", "低于该宽度时操作栏换行", SettingValueKind.Integer, 480, 360, 900, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("tasks.editor.compactActionsHeight", tasksUi, "操作栏矮窗断点", "低于该高度时操作栏换行", SettingValueKind.Integer, 680, 480, 1000, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("tasks.list.minColumnWidth", tasksUi, "列表最小列宽", "并排布局中任务列表最小宽度", SettingValueKind.Integer, 300, 220, 500, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("tasks.editor.notesMinHeight", tasksUi, "备注最小高度", "任务备注输入框最小高度", SettingValueKind.Integer, 128, 96, 360, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("tasks.button.minWidth", tasksUi, "按钮最小宽度", "任务操作按钮最小宽度", SettingValueKind.Integer, 96, 72, 160, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("tasks.button.minHeight", tasksUi, "按钮最小高度", "任务操作按钮最小高度", SettingValueKind.Integer, 40, 32, 56, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("startup.width", host, "启动宽度", "应用启动时的窗口宽度", SettingValueKind.Integer, 580, 420, 1200, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
            Definition("startup.height", host, "启动高度", "应用启动时的窗口高度", SettingValueKind.Integer, 260, 240, 800, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),

            Definition("storage.filename", host, "数据库文件名", "SQLite 数据库文件名", SettingValueKind.String, "mosaic.sqlite", null, null, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig, "改名后新库为空，旧库保留"),
            Definition("storage.location", host, "数据库位置", "数据库存储位置", SettingValueKind.Enum, "local-app-data", null, null, ["local-app-data"], SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig, "换位置不自动搬迁"),
            Definition("storage.queueCapacity", storage, "存储队列容量", "数据库后台队列容量", SettingValueKind.Integer, 128, 16, 4096, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings),
            Definition("storage.busyTimeoutSeconds", storage, "锁等待秒数", "数据库锁等待超时", SettingValueKind.Integer, 3, 1, 30, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings),
            Definition("storage.backupRetention", storage, "迁移备份保留数", "迁移失败备份最大保留数量", SettingValueKind.Integer, 5, 0, 50, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings),
            Definition("storage.journalMode", storage, "日志模式", "SQLite 日志模式", SettingValueKind.Enum, "Delete", null, null, ["Delete", "Wal"], SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "WAL 会产生 -wal/-shm 文件"),
            Definition("storage.connectionMode", storage, "连接模式", "数据库连接复用策略", SettingValueKind.Enum, "ReusedSingle", null, null, ["ReusedSingle", "PooledPerOperation"], SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings, "仍由单消费者队列串行执行"),
            Definition("host.modules", host, "启用模块", "重启时启用的模块列表", SettingValueKind.ModuleList, new[] { "storage-sqlite", "tasks-sqlite", "tasks-core", "tasks-ui" }, null, null, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig, "禁用模块可能使依赖或绑定无效"),
            Definition("host.providers", host, "能力绑定", "能力提供者映射", SettingValueKind.ProviderMap, new Dictionary<string, string>(StringComparer.Ordinal), null, null, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
            Definition("logging.level", host, "日志级别", "文件日志最低级别", SettingValueKind.Enum, "Information", null, null, ["Debug", "Information", "Warning", "Error", "None"], SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
            Definition("logging.retentionDays", host, "日志保留天数", "自动清理前的日志保留天数", SettingValueKind.Integer, 0, 0, 365, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
            Definition("diagnostics.singleInstanceRetryMilliseconds", host, "单实例重试间隔", "单实例监听失败后的重试间隔", SettingValueKind.Integer, 250, 50, 2000, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),

            Definition("ui.text.emptyAllTitle", text, "全部空态标题", "任务列表全部空态标题", SettingValueKind.String, "这里还没有任务", null, null, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("ui.text.emptyAllDescription", text, "全部空态说明", "任务列表全部空态说明", SettingValueKind.String, "新建一项任务，给今天一个开始。", null, null, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("ui.text.emptyOpenTitle", text, "未完成空态标题", "未完成任务空态标题", SettingValueKind.String, "没有未完成任务", null, null, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("ui.text.emptyOpenDescription", text, "未完成空态说明", "未完成任务空态说明", SettingValueKind.String, "新建任务或恢复一项任务后，会显示在这里。", null, null, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("ui.text.emptyCompletedTitle", text, "已完成空态标题", "已完成任务空态标题", SettingValueKind.String, "还没有已完成的任务", null, null, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("ui.text.emptyCompletedDescription", text, "已完成空态说明", "已完成任务空态说明", SettingValueKind.String, "完成一项任务后，会显示在这里。", null, null, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("ui.text.untitledTask", text, "未命名任务", "空标题任务在列表中的显示文字", SettingValueKind.String, "未命名任务", null, null, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("ui.text.saveSuccess", text, "保存成功文案", "任务保存成功提示", SettingValueKind.String, "已保存到此电脑", null, null, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("ui.text.deleteSuccess", text, "删除成功文案", "任务删除成功提示", SettingValueKind.String, "任务已删除", null, null, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("ui.text.completedSuccess", text, "标记完成文案", "任务标记完成提示", SettingValueKind.String, "已标记完成", null, null, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
            Definition("ui.text.restoredSuccess", text, "恢复未完成文案", "任务恢复未完成提示", SettingValueKind.String, "已恢复为未完成", null, null, null, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings)
        ];
    }

    private static SettingDefinition Definition(
        string id,
        string moduleId,
        string displayName,
        string description,
        SettingValueKind kind,
        object? defaultValue,
        object? min,
        object? max,
        IReadOnlyList<string>? allowed,
        SettingCategory category,
        SettingApplyMode applyMode,
        SettingPersistence persistence,
        string? warning = null) =>
        new(id, moduleId, displayName, description, kind, defaultValue, min, max, allowed, category, applyMode, persistence, warning);
}
