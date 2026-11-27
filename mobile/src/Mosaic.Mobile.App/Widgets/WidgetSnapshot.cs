using System.Globalization;
using Android.Content;
using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>One row the list factory renders: id, title and the two state flags.</summary>
internal sealed record WidgetRow(
    string Id,
    string Title,
    bool IsCompleted,
    bool IsArchived,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// The last rendered snapshot, persisted in a private preference so the
/// <see cref="TaskWidgetService"/> factory can rebuild any of the four groups
/// after the process was killed. Title and id are the only text kept; no token,
/// email or note is ever written here. The wire format itself lives in
/// <see cref="WidgetSnapshotCodec"/> so its upgrade path is unit-tested.
/// </summary>
internal static class WidgetSnapshotStore
{
    internal const string PreferencesFile = "mosaic.mobile.v3.widget";

    private const string RowsKey = "rows";

    private const string VersionKey = "rows.version";

    private static readonly object Gate = new();

    public static void Save(Context context, IReadOnlyList<WidgetTaskItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (Gate)
        {
            Preferences(context)?.Edit()
                ?.PutInt(VersionKey, WidgetSnapshotCodec.CurrentVersion)
                ?.PutString(RowsKey, WidgetSnapshotCodec.Serialize(items))
                ?.Commit();
        }
    }

    public static IReadOnlyList<WidgetTaskItem> LoadItems(Context context)
    {
        lock (Gate)
        {
            var preferences = Preferences(context);
            if (preferences is null)
            {
                return [];
            }

            return WidgetSnapshotCodec.Deserialize(
                preferences.GetString(RowsKey, null),
                preferences.GetInt(VersionKey, 0));
        }
    }

    private static ISharedPreferences? Preferences(Context context) =>
        context.GetSharedPreferences(PreferencesFile, FileCreationMode.Private);
}

/// <summary>Formats a snapshot item for the list factory.</summary>
internal static class WidgetRowFactory
{
    public static WidgetRow ToRow(WidgetTaskItem item) =>
        new(
            item.Id.ToString("D", CultureInfo.InvariantCulture),
            item.Title,
            item.IsCompleted,
            item.ArchivedAtUtc is not null,
            item.UpdatedAtUtc);
}
