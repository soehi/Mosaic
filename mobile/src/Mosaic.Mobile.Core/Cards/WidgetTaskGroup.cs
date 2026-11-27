namespace Mosaic.Mobile.Core.Cards;

/// <summary>
/// Which slice of the projection one widget instance shows. A widget carries a
/// single group at a time; the choice is stored per <c>appWidgetId</c> so two
/// widgets on the same home screen can show different groups.
/// </summary>
public enum WidgetTaskGroup
{
    Pending = 0,
    Completed,
    Archived,
    All,
}

/// <summary>Wire names, per-group empty text and the one rule about write access.</summary>
public static class WidgetTaskGroups
{
    /// <summary>Legacy value written by 0.3.0–0.3.2, where only pending rows existed.</summary>
    public const string LegacyDefaultWire = "open";

    public const string PendingWire = "pending";

    public const string CompletedWire = "completed";

    public const string ArchivedWire = "archived";

    public const string AllWire = "all";

    /// <summary>
    /// Rejects anything that is not one of the four groups, so a corrupted or
    /// crafted preference value falls back to <see cref="WidgetTaskGroup.Pending"/>
    /// instead of reaching an undefined branch.
    /// </summary>
    public static bool TryParse(string? raw, out WidgetTaskGroup group)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            // An absent value and the legacy "open" value both mean pending, so
            // an install upgrading from 0.3.2 keeps showing the same rows.
            case LegacyDefaultWire:
            case PendingWire:
                group = WidgetTaskGroup.Pending;
                return true;
            case CompletedWire:
                group = WidgetTaskGroup.Completed;
                return true;
            case ArchivedWire:
                group = WidgetTaskGroup.Archived;
                return true;
            case AllWire:
                group = WidgetTaskGroup.All;
                return true;
            default:
                group = WidgetTaskGroup.Pending;
                return false;
        }
    }

    public static string ToWire(WidgetTaskGroup group) => group switch
    {
        WidgetTaskGroup.Pending => PendingWire,
        WidgetTaskGroup.Completed => CompletedWire,
        WidgetTaskGroup.Archived => ArchivedWire,
        WidgetTaskGroup.All => AllWire,
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    /// <summary>The list's single row text when the group has nothing in it.</summary>
    public static string EmptyText(WidgetTaskGroup group) => group switch
    {
        WidgetTaskGroup.Pending => "暂无未完成任务",
        WidgetTaskGroup.Completed => "暂无已完成任务",
        WidgetTaskGroup.Archived => "暂无归档任务",
        WidgetTaskGroup.All => "暂无任务",
        _ => "暂无任务",
    };

    /// <summary>
    /// Only the pending list and the mixed list expose the complete circle.
    /// Completed and archived lists are read-only: un-completing, un-archiving
    /// and restoring are outside this client's action set, so their circles are
    /// drawn as a static filled check with no fill-in intent.
    /// </summary>
    public static bool AllowsComplete(WidgetTaskGroup group) =>
        group is WidgetTaskGroup.Pending or WidgetTaskGroup.All;

    public static string DisplayName(WidgetTaskGroup group) => group switch
    {
        WidgetTaskGroup.Pending => "未完成",
        WidgetTaskGroup.Completed => "已完成",
        WidgetTaskGroup.Archived => "已归档",
        WidgetTaskGroup.All => "全部",
        _ => group.ToString(),
    };
}

/// <summary>
/// One row of the widget snapshot. It is the smallest shape that can answer
/// every group: title, completion, archive time and the update time used for
/// ordering. No note, email, token or server address is ever part of it.
/// </summary>
public sealed record WidgetTaskItem(
    Guid Id,
    string Title,
    bool IsCompleted,
    DateTimeOffset? ArchivedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Group filtering and ordering. It lives in Core rather than in the Android
/// factory so the four group semantics are covered by ordinary unit tests.
/// </summary>
public static class WidgetTaskSelector
{
    /// <summary>
    /// Upper bound of one snapshot. The projection does not store
    /// <c>updatedAtUtc</c> as a column, so rows are read newest-created first
    /// and ordered by update time in memory; the cap keeps that read bounded.
    /// </summary>
    public const int MaxItems = 200;

    public static IReadOnlyList<WidgetTaskItem> Select(
        IReadOnlyList<WidgetTaskItem> items,
        WidgetTaskGroup group,
        int limit = MaxItems)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (limit <= 0)
        {
            return [];
        }

        // Archiving is immediate on the server: ArchivedAtUtc is either set or
        // null, so the groups partition the snapshot into two disjoint halves.
        var query = group switch
        {
            WidgetTaskGroup.Pending => items
                .Where(item => item.ArchivedAtUtc is null && !item.IsCompleted)
                .OrderByDescending(item => item.UpdatedAtUtc),
            WidgetTaskGroup.Completed => items
                .Where(item => item.ArchivedAtUtc is null && item.IsCompleted)
                .OrderByDescending(item => item.UpdatedAtUtc),
            WidgetTaskGroup.Archived => items
                .Where(item => item.ArchivedAtUtc is not null)
                .OrderByDescending(item => item.ArchivedAtUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(item => item.UpdatedAtUtc),
            _ => items
                .Where(item => item.ArchivedAtUtc is null)
                .OrderByDescending(item => item.UpdatedAtUtc),
        };

        // The id is a final tie-breaker so two tasks updated in the same
        // millisecond keep a stable order across refreshes.
        return query
            .ThenBy(item => item.Id)
            .Take(limit)
            .ToList();
    }
}
