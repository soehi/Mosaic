namespace Mosaic.Server.Sync;

/// <summary>
/// The server-side copy of the portable settings whitelist owned by the desktop
/// client (plan section 4.3). Only these keys may travel through backups or
/// incremental sync; anything else is rejected so secrets, machine paths and
/// host-only state can never reach the server.
/// </summary>
public static class PortableSettings
{
    public static readonly IReadOnlyList<string> Keys =
    [
        "ui.language",
        "appearance.theme",
        "tasks.sortOrder",
        "tasks.defaultFilter",
        "tasks.archive.autoEnabled",
        "tasks.archive.delayDays",
        "tasks.title.maxCharacters",
        "tasks.notes.maxCharacters",
        "tasks.title.required",
        "tasks.defaultCompleted",
        "tasks.confirmDelete",
        "tasks.confirmUnsaved",
        "tasks.showCharacterCount",
        "tasks.autoSwitchToAllOnHidden",
    ];

    private static readonly HashSet<string> Known = new(Keys, StringComparer.Ordinal);

    public static bool IsAllowed(string key) => Known.Contains(key);
}
