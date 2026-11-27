using System.Globalization;
using System.Text;
using Mosaic.Mobile.Core.Projection;

namespace Mosaic.Mobile.Core.Cards;

/// <summary>
/// The on-disk row format of the widget snapshot. It lives in Core so the
/// upgrade path is covered by ordinary unit tests: 0.3.2 wrote three fields per
/// row (id, title, completed) with no version marker, and 0.3.3 writes six
/// fields behind <see cref="CurrentVersion"/>. A pre-0.3.3 string is rejected
/// rather than reinterpreted, because its third field would be read as an
/// archive flag and silently move tasks between groups.
/// </summary>
public static class WidgetSnapshotCodec
{
    public const int CurrentVersion = 4;

    private const char FieldSeparator = '\u001F';

    private const char RowSeparator = '\u001E';

    private const int FieldCount = 6;

    public static string Serialize(IReadOnlyList<WidgetTaskItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var builder = new StringBuilder();
        for (var index = 0; index < items.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(RowSeparator);
            }

            var item = items[index];
            builder.Append(item.Id.ToString("D")).Append(FieldSeparator)
                .Append(Sanitize(item.Title)).Append(FieldSeparator)
                .Append(item.IsCompleted ? '1' : '0').Append(FieldSeparator)
                .Append(item.ArchivedAtUtc is null ? '0' : '1').Append(FieldSeparator)
                .Append(item.UpdatedAtUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)).Append(FieldSeparator)
                .Append((item.ArchivedAtUtc ?? item.UpdatedAtUtc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses a snapshot written at <paramref name="version"/>.
    /// <para>
    /// Version 0 is the 0.3.2 file, which wrote no version key and three fields
    /// per row. Those rows were already filtered to unfinished, unarchived tasks
    /// when written, so they migrate as pending rows with no timestamps instead
    /// of being discarded — an upgrading install keeps its card populated and the
    /// first refresh replaces the file with the current shape.
    /// </para>
    /// <para>
    /// Any other version yields an empty list: the card then shows its empty
    /// state until the next refresh, which is the only safe option for a shape
    /// this build cannot interpret.
    /// </para>
    /// </summary>
    public static IReadOnlyList<WidgetTaskItem> Deserialize(string? raw, int version)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }

        if (version == 0)
        {
            return DeserializeLegacy(raw);
        }

        if (version != CurrentVersion)
        {
            return [];
        }

        var items = new List<WidgetTaskItem>();
        foreach (var chunk in raw.Split(RowSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = chunk.Split(FieldSeparator);
            if (fields.Length != FieldCount
                || !Guid.TryParse(fields[0], out var id)
                || !long.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var updatedTicks)
                || !long.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var archivedTicks))
            {
                continue;
            }

            items.Add(new WidgetTaskItem(
                id,
                fields[1],
                fields[2] == "1",
                fields[3] == "1" ? DateTimeOffset.FromUnixTimeMilliseconds(archivedTicks) : null,
                DateTimeOffset.FromUnixTimeMilliseconds(updatedTicks)));
        }

        return items;
    }

    /// <summary>
    /// The 0.3.2 row shape: <c>id\u001Ftitle\u001Fcompleted</c>. A row that fails
    /// to parse is skipped; an unreadable timestamp becomes
    /// <see cref="DateTimeOffset.UnixEpoch"/> so the migrated rows keep a stable
    /// order until the first refresh supplies real times.
    /// </summary>
    private static IReadOnlyList<WidgetTaskItem> DeserializeLegacy(string raw)
    {
        var items = new List<WidgetTaskItem>();
        foreach (var chunk in raw.Split(RowSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = chunk.Split(FieldSeparator);
            if (fields.Length != 3 || !Guid.TryParse(fields[0], out var id))
            {
                continue;
            }

            items.Add(new WidgetTaskItem(
                id,
                fields[1],
                fields[2] == "1",
                null,
                DateTimeOffset.UnixEpoch));
        }

        return items;
    }

    private static string Sanitize(string? value) =>
        (value ?? string.Empty)
            .Replace(FieldSeparator, ' ')
            .Replace(RowSeparator, ' ');
}

/// <summary>
/// Turns a projection read into the bounded widget snapshot. It lives here
/// rather than in the Android project so the mapping (including the archive
/// precedence between payload and column) is unit-tested.
/// </summary>
public static class WidgetSnapshotFactory
{
    public static IReadOnlyList<WidgetTaskItem> ToItems(IReadOnlyList<SyncedTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var items = new List<WidgetTaskItem>(Math.Min(tasks.Count, WidgetTaskSelector.MaxItems));
        foreach (var task in tasks)
        {
            if (!TaskPayload.TryRead(task.PayloadJson, out var fields, out _))
            {
                continue;
            }

            items.Add(new WidgetTaskItem(
                task.Id,
                task.Title,
                fields.IsCompleted,
                fields.ArchivedAtUtc ?? task.ArchivedAtUtc,
                fields.UpdatedAtUtc));
            if (items.Count == WidgetTaskSelector.MaxItems)
            {
                break;
            }
        }

        return items;
    }
}
