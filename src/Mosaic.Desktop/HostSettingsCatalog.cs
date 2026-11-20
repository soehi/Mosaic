using Mosaic.Settings.Contracts;

namespace Mosaic.Desktop;

internal static class HostSettingsCatalog
{
    internal const string ModuleId = "host";
    internal const string DefaultStorageFilename = "mosaic.sqlite";
    internal const string DefaultStorageLocation = "local-app-data";

    internal static IReadOnlyList<SettingDefinition> Create()
    {
        var definitions = new[]
        {
            Integer("shell.window.minWidth", "Settings.Setting.WindowMinWidth", 1100, 1000, 2400,
                "Settings.Setting.WindowMinWidth.Description"),
            Integer("shell.window.minHeight", "Settings.Setting.WindowMinHeight", 720, 640, 1200,
                "Settings.Setting.WindowMinHeight.Description"),
            Integer("startup.width", "Settings.Setting.StartupWidth", 1504, 1000, 3840,
                "Settings.Setting.StartupWidth.Description"),
            Integer("startup.height", "Settings.Setting.StartupHeight", 1012, 640, 2160,
                "Settings.Setting.StartupHeight.Description"),
            Text("storage.filename", "Settings.Setting.DatabaseFilename", DefaultStorageFilename,
                "数据库文件名", "SQLite 数据库文件名", "Settings.Setting.DatabaseFilename.Description"),
            // storage.location stays a host override written by the maintenance page after a verified
            // migration; editing it as plain text created an empty database at the new path.
            new SettingDefinition("host.modules", ModuleId, "启用模块", "重启时启用的模块列表",
                SettingValueKind.ModuleList, BuiltInModuleCatalog.DefaultModules.ToArray(), null, null, null,
                SettingsGroup.StorageSystem, SettingCategory.Advanced, SettingApplyMode.Restart,
                SettingPersistence.HostConfig, "禁用模块可能使依赖或绑定无效",
                DisplayNameKey: "Settings.Setting.EnabledModules",
                DescriptionKey: "Settings.Setting.EnabledModules.Description"),
            new SettingDefinition("host.providers", ModuleId, "能力绑定", "能力提供者映射",
                SettingValueKind.ProviderMap,
                new Dictionary<string, string>(BuiltInModuleCatalog.DefaultProviders, StringComparer.Ordinal),
                null, null, null, SettingsGroup.StorageSystem, SettingCategory.Advanced,
                SettingApplyMode.Restart, SettingPersistence.HostConfig,
                DisplayNameKey: "Settings.Setting.ProviderBindings",
                DescriptionKey: "Settings.Setting.ProviderBindings.Description"),
            Enum("logging.level", "Settings.Setting.LogLevel", "Information",
                ["Debug", "Information", "Warning", "Error", "None"]),
            new SettingDefinition("startup.runAtLogon", ModuleId, "登录后启动 Mosaic", "Windows 登录时使用当前工作区自动启动 Mosaic",
                SettingValueKind.Boolean, false, null, null, null,
                SettingsGroup.StorageSystem, SettingCategory.Normal, SettingApplyMode.Immediate,
                SettingPersistence.HostConfig,
                DisplayNameKey: "Settings.Setting.RunAtLogon", DescriptionKey: "Settings.Setting.RunAtLogon.Description"),
            Integer("logging.retentionDays", "Settings.Setting.LogRetention", 0, 0, 365,
                "Settings.Setting.LogRetention.Description"),
            Integer("diagnostics.singleInstanceRetryMilliseconds", "Settings.Setting.SingleInstanceRetry", 250, 50, 2000,
                "Settings.Setting.SingleInstanceRetry.Description")
        };
        return definitions;
    }

    private static SettingDefinition Integer(
        string id,
        string displayNameKey,
        int defaultValue,
        int minimum,
        int maximum,
        string descriptionKey) =>
        new(id, ModuleId, id, id, SettingValueKind.Integer, defaultValue, minimum, maximum, null,
            SettingsGroup.StorageSystem, SettingCategory.Advanced, SettingApplyMode.Restart,
            SettingPersistence.HostConfig, DisplayNameKey: displayNameKey, DescriptionKey: descriptionKey);

    private static SettingDefinition Text(
        string id,
        string displayNameKey,
        string defaultValue,
        string displayName,
        string description,
        string descriptionKey) =>
        new(id, ModuleId, displayName, description, SettingValueKind.String, defaultValue, null, null, null,
            SettingsGroup.StorageSystem, SettingCategory.Advanced, SettingApplyMode.Restart,
            SettingPersistence.HostConfig, DisplayNameKey: displayNameKey, DescriptionKey: descriptionKey);

    private static SettingDefinition Enum(
        string id,
        string displayNameKey,
        string defaultValue,
        IReadOnlyList<string> allowedValues) =>
        new(id, ModuleId, id, id, SettingValueKind.Enum, defaultValue, null, null, allowedValues,
            SettingsGroup.StorageSystem, SettingCategory.Advanced, SettingApplyMode.Restart,
            SettingPersistence.HostConfig,
            DisplayNameKey: displayNameKey,
            DescriptionKey: displayNameKey + ".Description",
            Options: allowedValues.Select(value => new SettingOption(value, "Setting.Value." + value)).ToArray());
}
