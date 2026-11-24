using Mosaic.Settings.Contracts;

namespace Mosaic.Sync.Core.Internal;

internal sealed class SyncOptions
{
    public bool Enabled { get; private set; } = true;
    public bool IncludeSettings { get; private set; } = true;
    public bool IncludeAttachments { get; private set; } = true;

    internal void Apply(IReadOnlyDictionary<string, object?> values)
    {
        Enabled = values.GetValueOrDefault(SyncSettingsSource.Enabled) is not false;
        IncludeSettings = values.GetValueOrDefault(SyncSettingsSource.IncludeSettings) is not false;
        IncludeAttachments = values.GetValueOrDefault(SyncSettingsSource.IncludeAttachments) is not false;
    }
}

internal sealed class SyncSettingsSource(SyncOptions options) : ISettingSource
{
    internal const string Enabled = "sync.enabled";
    internal const string IncludeSettings = "sync.includeSettings";
    internal const string IncludeAttachments = "sync.includeAttachments";

    public string ModuleId => "sync-core";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(Enabled, "sync-core", "启用账号备份", "登录账号后可手动上传和下载备份；关闭后账号页只显示本地数据。",
            SettingValueKind.Boolean, true, null, null, null, SettingsGroup.StorageSystem, SettingCategory.Normal,
            SettingApplyMode.Immediate, SettingPersistence.UserSettings,
            DisplayNameKey: "Settings.Setting.SyncEnabled", DescriptionKey: "Settings.Setting.SyncEnabled.Description"),
        new(IncludeSettings, "sync-core", "备份用户设置", "允许备份任务规则、任务文案、外观主题与界面语言；关闭后备份只包含任务。主机配置、窗口位置、驻留与启动设置、账号凭据永远不参与备份。",
            SettingValueKind.Boolean, true, null, null, null, SettingsGroup.StorageSystem, SettingCategory.Normal,
            SettingApplyMode.Immediate, SettingPersistence.UserSettings,
            DisplayNameKey: "Settings.Setting.SyncIncludeSettings", DescriptionKey: "Settings.Setting.SyncIncludeSettings.Description"),
        new(IncludeAttachments, "sync-core", "备份任务附件", "允许手动上传和下载任务附件；关闭后备份只包含任务本身，下载恢复会清空本地附件记录并保留磁盘文件。",
            SettingValueKind.Boolean, true, null, null, null, SettingsGroup.StorageSystem, SettingCategory.Normal,
            SettingApplyMode.Immediate, SettingPersistence.UserSettings,
            DisplayNameKey: "Settings.Setting.SyncIncludeAttachments", DescriptionKey: "Settings.Setting.SyncIncludeAttachments.Description")
    ];

    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        options.Apply(values);
        return Task.CompletedTask;
    }
}
