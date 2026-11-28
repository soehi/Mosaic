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

    public static bool Save(Context context, IReadOnlyList<WidgetTaskItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (Gate)
        {
            return Preferences(context)?.Edit()
                ?.PutInt(VersionKey, WidgetSnapshotCodec.CurrentVersion)
                ?.PutString(RowsKey, WidgetSnapshotCodec.Serialize(items))
                ?.Commit() == true;
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

    // 只有旧种子偏好存在时，才清理该构建专用的六个固定 ID。
    public static void RemoveRetiredFixture(Context context)
    {
        const string retiredPreference = "mosaic.mobile.v3.testseed";
        if (context.GetSharedPreferences(retiredPreference, FileCreationMode.Private)?.All is not { Count: > 0 })
        {
            return;
        }

        lock (Gate)
        {
            var items = LoadItems(context);
            var fixtureIds = Enumerable.Range(1, 6)
                .Select(index => new Guid($"a1000000-0000-4000-8000-{index:D12}"))
                .ToHashSet();
            var kept = items.Where(item => !fixtureIds.Contains(item.Id)).ToArray();
            if (kept.Length != items.Count && Preferences(context)?.Edit()
                ?.PutInt(VersionKey, WidgetSnapshotCodec.CurrentVersion)
                ?.PutString(RowsKey, WidgetSnapshotCodec.Serialize(kept))?.Commit() != true)
            {
                return;
            }
            context.DeleteSharedPreferences(retiredPreference);
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
