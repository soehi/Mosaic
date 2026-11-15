using Mosaic.Settings.Contracts;

namespace Mosaic.Settings.Core;

public static class SettingCatalog
{
    public static IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        Definition("shell.window.minWidth", "host", "窗口最小宽度", "允许的最小窗口宽度", SettingValueKind.Integer, 760, 640, 1200, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
        Definition("shell.window.minHeight", "host", "窗口最小高度", "允许的最小窗口高度", SettingValueKind.Integer, 620, 520, 900, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
        Definition("startup.width", "host", "启动宽度", "应用启动时的窗口宽度", SettingValueKind.Integer, 580, 420, 1200, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
        Definition("startup.height", "host", "启动高度", "应用启动时的窗口高度", SettingValueKind.Integer, 260, 240, 800, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
        Definition("storage.filename", "host", "数据库文件名", "SQLite 数据库文件名", SettingValueKind.String, "mosaic.sqlite", null, null, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig, "改名后新库为空，旧库保留"),
        Definition("storage.location", "host", "数据库位置", "数据库存储位置", SettingValueKind.String, "local-app-data", null, null, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig, "换位置不自动搬迁"),
        Definition("host.modules", "host", "启用模块", "重启时启用的模块列表", SettingValueKind.ModuleList, new[] { "storage-sqlite", "settings-sqlite", "settings-core", "tasks-sqlite", "tasks-core", "tasks-ui", "settings-ui" }, null, null, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig, "禁用模块可能使依赖或绑定无效"),
        Definition("host.providers", "host", "能力绑定", "能力提供者映射", SettingValueKind.ProviderMap, new Dictionary<string, string>(StringComparer.Ordinal), null, null, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
        Definition("logging.level", "host", "日志级别", "文件日志最低级别", SettingValueKind.Enum, "Information", null, null, ["Debug", "Information", "Warning", "Error", "None"], SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
        Definition("logging.retentionDays", "host", "日志保留天数", "自动清理前的日志保留天数", SettingValueKind.Integer, 0, 0, 365, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig),
        Definition("diagnostics.singleInstanceRetryMilliseconds", "host", "单实例重试间隔", "单实例监听失败后的重试间隔", SettingValueKind.Integer, 250, 50, 2000, null, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig)
    ];

    public static IReadOnlyList<SettingDefinition> ForHost() =>
        Definitions.Where(item => item.Persistence == SettingPersistence.HostConfig).ToArray();

    public static IReadOnlyList<SettingOption> OptionsFor(string settingId) => settingId switch
    {
        "appearance.theme" => Options(
            ("FollowSystem", "Setting.Value.FollowSystem"),
            ("Light", "Setting.Value.Light"),
            ("Dark", "Setting.Value.Dark"),
            ("HighContrast", "Setting.Value.HighContrast")),
        "ui.language" => Options(
            ("FollowSystem", "Setting.Value.FollowSystemLanguage"),
            ("Chinese", "Setting.Value.Chinese"),
            ("English", "Setting.Value.English")),
        "tasks.defaultFilter" => Options(
            ("All", "Setting.Value.All"),
            ("Open", "Setting.Value.Open"),
            ("Completed", "Setting.Value.Completed")),
        "tasks.sortOrder" => Options(
            ("CreatedDesc", "Setting.Value.CreatedDesc"),
            ("CreatedAsc", "Setting.Value.CreatedAsc"),
            ("TitleAsc", "Setting.Value.TitleAsc"),
            ("TitleDesc", "Setting.Value.TitleDesc")),
        "storage.journalMode" => Options(
            ("Delete", "Setting.Value.Delete"),
            ("Wal", "Setting.Value.Wal")),
        "storage.connectionMode" => Options(
            ("ReusedSingle", "Setting.Value.ReusedSingle"),
            ("PooledPerOperation", "Setting.Value.PooledPerOperation")),
        "logging.level" => Options(
            ("Debug", "Setting.Value.Debug"),
            ("Information", "Setting.Value.Information"),
            ("Warning", "Setting.Value.Warning"),
            ("Error", "Setting.Value.Error"),
            ("None", "Setting.Value.None")),
        _ => []
    };

    private static IReadOnlyList<SettingOption> Options(params (string Value, string DisplayNameKey)[] options) =>
        options.Select(item => new SettingOption(item.Value, item.DisplayNameKey)).ToArray();

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
