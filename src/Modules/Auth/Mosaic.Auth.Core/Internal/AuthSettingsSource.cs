using Mosaic.Settings.Contracts;

namespace Mosaic.Auth.Core.Internal;

// Only non-secret connection data lives here. Passwords, codes and tokens never reach the
// settings store, the commit journal or the settings UI.
internal sealed class AuthSettingsSource(AuthService service) : ISettingSource
{
    internal const string ServerUrl = "auth.serverUrl";

    public string ModuleId => "auth-core";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(ServerUrl, "auth-core", "账号服务器地址", "用于登录与备份的自建服务器地址，默认 https://mosaic.bansonic.top；留空表示不连接服务器。",
            SettingValueKind.String, "", null, null, null, SettingsGroup.StorageSystem, SettingCategory.Normal,
            SettingApplyMode.Immediate, SettingPersistence.UserSettings,
            // The start page owns the server choice; the generic editor must not offer a second input.
            // The definition stays registered so AuthService can still read and save the address.
            DisplayNameKey: "Settings.Setting.AuthServer", DescriptionKey: "Settings.Setting.AuthServer.Description",
            ShowInSettings: false)
    ];

    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        service.ApplyServerSettingAsync(values, cancellationToken);
}
