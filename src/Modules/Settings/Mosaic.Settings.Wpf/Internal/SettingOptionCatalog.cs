using Mosaic.Settings.Contracts;

namespace Mosaic.Settings.Wpf.Internal;

internal static class SettingOptionCatalog
{
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
}
