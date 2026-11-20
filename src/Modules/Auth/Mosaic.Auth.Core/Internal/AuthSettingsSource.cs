using Mosaic.Settings.Contracts;

namespace Mosaic.Auth.Core.Internal;

// Only non-secret connection data lives here. Passwords, codes and tokens never reach the
// settings store, the commit journal or the settings UI.
internal sealed class AuthSettingsSource(AuthService service) : ISettingSource
{
    internal const string ServerUrl = "auth.serverUrl";
    // True once the user has made a first-run account choice (sign in or go offline); fresh
    // installs start with no value and are asked once, existing installs migrate to true.
    internal const string AccountChoiceMade = "auth.accountChoiceMade";

    public string ModuleId => "auth-core";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(ServerUrl, "auth-core", "账号服务器地址", "用于登录与备份的自建服务器地址，默认 https://mosaic.bansonic.top；留空表示不连接服务器。",
            SettingValueKind.String, "", null, null, null, SettingsGroup.StorageSystem, SettingCategory.Normal,
            SettingApplyMode.Immediate, SettingPersistence.UserSettings,
            DisplayNameKey: "Settings.Setting.AuthServer", DescriptionKey: "Settings.Setting.AuthServer.Description"),
        new(AccountChoiceMade, "auth-core", "账户选择已完成", "记录首次启动时的账户选择。",
            SettingValueKind.Boolean, false, null, null, null, SettingsGroup.AccountsAndBackup, SettingCategory.Advanced,
            SettingApplyMode.Immediate, SettingPersistence.UserSettings)
    ];

    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        service.ApplyServerSettingAsync(values, cancellationToken);
}
