using System.Globalization;
using Mosaic.Settings.Contracts;

namespace Mosaic.Sync.Core.Internal;

internal sealed class SyncOptions
{
    internal const int DefaultAutoIntervalSeconds = 60;
    internal const int MinAutoIntervalSeconds = 15;
    internal const int MaxAutoIntervalSeconds = 86400;

    public bool Enabled { get; private set; } = true;
    public bool IncludeSettings { get; private set; } = true;
    public bool IncludeAttachments { get; private set; } = true;
    public bool AutoSyncEnabled { get; private set; } = true;
    public int AutoIntervalSeconds { get; private set; } = DefaultAutoIntervalSeconds;

    /// <summary>周期自动备份的等待间隔；循环每轮都以当前值重新计时，改设置后下一轮即生效。</summary>
    public TimeSpan AutoInterval => TimeSpan.FromSeconds(AutoIntervalSeconds);

    internal void Apply(IReadOnlyDictionary<string, object?> values)
    {
        Enabled = values.GetValueOrDefault(SyncSettingsSource.Enabled) is not false;
        IncludeSettings = values.GetValueOrDefault(SyncSettingsSource.IncludeSettings) is not false;
        IncludeAttachments = values.GetValueOrDefault(SyncSettingsSource.IncludeAttachments) is not false;
        AutoSyncEnabled = values.GetValueOrDefault(SyncSettingsSource.AutoSyncEnabled) is not false;
        AutoIntervalSeconds = ParseAutoInterval(values.GetValueOrDefault(SyncSettingsSource.AutoIntervalSeconds));
    }

    // 设置服务已按 Min/Max 校验过写入值；这里仍做一次兜底，旧配置里的非法值不会把循环拖进异常间隔。
    private static int ParseAutoInterval(object? value)
    {
        if (value is null) return DefaultAutoIntervalSeconds;
        long seconds;
        try { seconds = Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return DefaultAutoIntervalSeconds;
        }
        return (int)Math.Clamp(seconds, MinAutoIntervalSeconds, MaxAutoIntervalSeconds);
    }
}

internal sealed class SyncSettingsSource(SyncOptions options, Action? onTimingChanged = null) : ISettingSource
{
    internal const string Enabled = "sync.enabled";
    internal const string IncludeSettings = "sync.includeSettings";
    internal const string IncludeAttachments = "sync.includeAttachments";
    internal const string AutoSyncEnabled = "sync.autoSyncEnabled";
    internal const string AutoIntervalSeconds = "sync.autoIntervalSeconds";

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
            DisplayNameKey: "Settings.Setting.SyncIncludeAttachments", DescriptionKey: "Settings.Setting.SyncIncludeAttachments.Description"),
        new(AutoSyncEnabled, "sync-core", "自动备份", "保持登录时按下面的间隔自动上传任务与用户设置；自动备份只上传、不下载，也不镜像附件。关闭后只能从账户页手动备份。",
            SettingValueKind.Boolean, true, null, null, null, SettingsGroup.StorageSystem, SettingCategory.Normal,
            SettingApplyMode.Immediate, SettingPersistence.UserSettings,
            DisplayNameKey: "Settings.Setting.SyncAutoEnabled", DescriptionKey: "Settings.Setting.SyncAutoEnabled.Description"),
        new(AutoIntervalSeconds, "sync-core", "自动备份间隔", "两次自动备份之间的秒数；账号未登录或备份功能关闭时不会上传。间隔改小会增加网络与数据库读取频率。",
            SettingValueKind.Integer, SyncOptions.DefaultAutoIntervalSeconds, SyncOptions.MinAutoIntervalSeconds, SyncOptions.MaxAutoIntervalSeconds, null,
            SettingsGroup.StorageSystem, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings,
            DisplayNameKey: "Settings.Setting.SyncAutoIntervalSeconds", DescriptionKey: "Settings.Setting.SyncAutoIntervalSeconds.Description")
    ];

    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        var autoSyncEnabled = options.AutoSyncEnabled;
        var intervalSeconds = options.AutoIntervalSeconds;
        options.Apply(values);
        // 开关或间隔真的变了才打断后台等待；其余设置提交不影响自动备份的计时。
        if (autoSyncEnabled != options.AutoSyncEnabled || intervalSeconds != options.AutoIntervalSeconds)
            onTimingChanged?.Invoke();
        return Task.CompletedTask;
    }
}