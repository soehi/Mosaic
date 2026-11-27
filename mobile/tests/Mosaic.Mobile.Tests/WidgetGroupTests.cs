using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.Tests;

/// <summary>
/// The four groups are a widget-only read model. The tests pin the boundaries
/// (which rows each group holds), the ordering, the legacy preference value and
/// the fact that only the pending and mixed lists may issue a write.
/// </summary>
public sealed class WidgetGroupTests
{
    private static readonly DateTimeOffset Base = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<WidgetTaskItem> Items =
    [
        Item("a1000000-0000-4000-8000-000000000001", "pending-old", false, 1),
        Item("a1000000-0000-4000-8000-000000000002", "pending-new", false, 5),
        Item("a1000000-0000-4000-8000-000000000003", "completed", true, 2),
        Item("a1000000-0000-4000-8000-000000000004", "archived-pending", false, 3, 4),
        Item("a1000000-0000-4000-8000-000000000005", "archived-completed", true, 4, 5),
    ];

    [Fact]
    public void Pending_holds_unfinished_unarchived_tasks_newest_first()
    {
        var rows = WidgetTaskSelector.Select(Items, WidgetTaskGroup.Pending);

        Assert.Equal(["pending-new", "pending-old"], rows.Select(row => row.Title));
    }

    [Fact]
    public void Completed_excludes_archived_tasks()
    {
        var rows = WidgetTaskSelector.Select(Items, WidgetTaskGroup.Completed);

        Assert.Equal(["completed"], rows.Select(row => row.Title));
    }

    [Fact]
    public void Archived_holds_only_archived_tasks_newest_archive_first()
    {
        var rows = WidgetTaskSelector.Select(Items, WidgetTaskGroup.Archived);

        Assert.Equal(["archived-completed", "archived-pending"], rows.Select(row => row.Title));
    }

    [Fact]
    public void All_is_pending_plus_completed_and_never_archived()
    {
        var rows = WidgetTaskSelector.Select(Items, WidgetTaskGroup.All);

        Assert.Equal(["pending-new", "completed", "pending-old"], rows.Select(row => row.Title));
    }

    [Fact]
    public void Groups_partition_the_snapshot_without_overlap()
    {
        var pending = WidgetTaskSelector.Select(Items, WidgetTaskGroup.Pending).Select(row => row.Id);
        var completed = WidgetTaskSelector.Select(Items, WidgetTaskGroup.Completed).Select(row => row.Id);
        var archived = WidgetTaskSelector.Select(Items, WidgetTaskGroup.Archived).Select(row => row.Id);

        var union = pending.Concat(completed).Concat(archived).ToList();
        Assert.Equal(Items.Count, union.Count);
        Assert.Equal(Items.Count, union.Distinct().Count());
    }

    [Fact]
    public void Only_pending_and_all_offer_the_complete_action()
    {
        Assert.True(WidgetTaskGroups.AllowsComplete(WidgetTaskGroup.Pending));
        Assert.True(WidgetTaskGroups.AllowsComplete(WidgetTaskGroup.All));
        Assert.False(WidgetTaskGroups.AllowsComplete(WidgetTaskGroup.Completed));
        Assert.False(WidgetTaskGroups.AllowsComplete(WidgetTaskGroup.Archived));
    }

    [Theory]
    [InlineData("pending", WidgetTaskGroup.Pending, true)]
    [InlineData("open", WidgetTaskGroup.Pending, true)]
    [InlineData("PENDING", WidgetTaskGroup.Pending, true)]
    [InlineData("completed", WidgetTaskGroup.Completed, true)]
    [InlineData("archived", WidgetTaskGroup.Archived, true)]
    [InlineData("all", WidgetTaskGroup.All, true)]
    [InlineData(null, WidgetTaskGroup.Pending, false)]
    [InlineData("", WidgetTaskGroup.Pending, false)]
    [InlineData("garbage", WidgetTaskGroup.Pending, false)]
    public void Parse_accepts_the_four_groups_and_falls_back_to_pending(
        string? raw,
        WidgetTaskGroup expected,
        bool parsed)
    {
        Assert.Equal(parsed, WidgetTaskGroups.TryParse(raw, out var group));
        Assert.Equal(expected, group);
    }

    [Fact]
    public void Every_group_round_trips_through_its_wire_name()
    {
        foreach (var group in Enum.GetValues<WidgetTaskGroup>())
        {
            Assert.True(WidgetTaskGroups.TryParse(WidgetTaskGroups.ToWire(group), out var parsed));
            Assert.Equal(group, parsed);
        }
    }

    [Fact]
    public void Each_group_has_its_own_empty_text()
    {
        var texts = Enum.GetValues<WidgetTaskGroup>().Select(WidgetTaskGroups.EmptyText).ToList();

        Assert.Equal(texts.Count, texts.Distinct().Count());
        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
    }

    [Fact]
    public void Select_respects_an_explicit_limit()
    {
        var rows = WidgetTaskSelector.Select(Items, WidgetTaskGroup.All, limit: 2);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Select_returns_nothing_for_a_non_positive_limit()
    {
        Assert.Empty(WidgetTaskSelector.Select(Items, WidgetTaskGroup.Pending, limit: 0));
    }

    [Fact]
    public void A_group_preserves_rows_beyond_the_initial_widget_viewport()
    {
        var tasks = Enumerable.Range(1, 4)
            .Select(index => Item($"65432100-0000-4000-8000-{index:D12}", $"Task {index}", false, -index))
            .ToArray();

        var rows = WidgetTaskSelector.Select(tasks, WidgetTaskGroup.Pending);

        Assert.Equal(tasks.Select(task => task.Id), rows.Select(row => row.Id));
    }

    private static WidgetTaskItem Item(
        string id,
        string title,
        bool completed,
        int minutes,
        int? minutesArchive = null) =>
        new(
            Guid.Parse(id),
            title,
            completed,
            minutesArchive is null ? null : Base.AddMinutes(minutesArchive.Value),
            Base.AddMinutes(minutes));
}
