using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.Tests;

/// <summary>
/// The snapshot wire format is the only persisted widget state, so its upgrade
/// path matters: a 0.3.2 install has three fields per row, and reinterpreting
/// those as the 0.3.3 shape would mislabel completed and archived tasks.
/// </summary>
public sealed class WidgetSnapshotCodecTests
{
    private static readonly DateTimeOffset Base = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Round_trip_preserves_every_field()
    {
        IReadOnlyList<WidgetTaskItem> items =
        [
            new(Guid.Parse("a1000000-0000-4000-8000-000000000001"), "整理周报", false, null, Base),
            new(Guid.Parse("a1000000-0000-4000-8000-000000000002"), "已完成的", true, null, Base.AddMinutes(3)),
            new(Guid.Parse("a1000000-0000-4000-8000-000000000003"), "已归档的", false, Base.AddMinutes(7), Base.AddMinutes(5)),
        ];

        var restored = WidgetSnapshotCodec.Deserialize(
            WidgetSnapshotCodec.Serialize(items),
            WidgetSnapshotCodec.CurrentVersion);

        Assert.Equal(items, restored);
    }

    [Fact]
    public void A_0_3_2_file_migrates_as_pending_rows()
    {
        var legacy = string.Join(
            '\u001E',
            "a1000000-0000-4000-8000-000000000001\u001F整理周报\u001F0",
            "a1000000-0000-4000-8000-000000000002\u001F写总结\u001F0");

        var restored = WidgetSnapshotCodec.Deserialize(legacy, version: 0);

        Assert.Equal(2, restored.Count);
        Assert.All(restored, item =>
        {
            Assert.False(item.IsCompleted);
            Assert.Null(item.ArchivedAtUtc);
        });
        Assert.Equal(["整理周报", "写总结"], restored.Select(item => item.Title));
    }

    [Fact]
    public void An_unknown_version_is_never_reinterpreted()
    {
        var raw = "a1000000-0000-4000-8000-000000000001\u001F整理周报\u001F1";

        Assert.Empty(WidgetSnapshotCodec.Deserialize(raw, version: 3));
        Assert.Empty(WidgetSnapshotCodec.Deserialize(raw, version: 5));
    }

    [Fact]
    public void Malformed_rows_are_dropped_without_failing_the_snapshot()
    {
        var valid = WidgetSnapshotCodec.Serialize(
            [new WidgetTaskItem(Guid.NewGuid(), "有效", false, null, Base)]);
        var raw = "\u001Enot-a-guid\u001Fx\u001F0".Replace('\u001E', '\0') + "\u001E" + valid;

        var restored = WidgetSnapshotCodec.Deserialize(raw, WidgetSnapshotCodec.CurrentVersion);

        Assert.Single(restored);
        Assert.Equal("有效", restored[0].Title);
    }

    [Fact]
    public void Separators_inside_a_title_cannot_forge_extra_fields()
    {
        var item = new WidgetTaskItem(Guid.NewGuid(), "a\u001Fb\u001Ec", true, null, Base);

        var restored = WidgetSnapshotCodec.Deserialize(
            WidgetSnapshotCodec.Serialize([item]),
            WidgetSnapshotCodec.CurrentVersion);

        Assert.Single(restored);
        Assert.Equal("a b c", restored[0].Title);
        Assert.True(restored[0].IsCompleted);
    }

    [Fact]
    public void An_empty_snapshot_serializes_and_parses_as_empty()
    {
        Assert.Empty(WidgetSnapshotCodec.Deserialize(WidgetSnapshotCodec.Serialize([]), WidgetSnapshotCodec.CurrentVersion));
        Assert.Empty(WidgetSnapshotCodec.Deserialize(null, WidgetSnapshotCodec.CurrentVersion));
        Assert.Empty(WidgetSnapshotCodec.Deserialize(string.Empty, WidgetSnapshotCodec.CurrentVersion));
    }
}