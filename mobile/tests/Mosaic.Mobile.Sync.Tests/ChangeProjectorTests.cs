using System.Text.Json;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync.Tests;

/// <summary>
/// Contract tests for <see cref="ChangeProjector"/>: canonical 0.1.1 rows,
/// same-page authority order, and every strict rejection. Anything the client
/// cannot interpret exactly must throw <see cref="SyncProtocolException"/> so
/// the whole page is discarded and the stored cursor cannot advance past it.
/// </summary>
public sealed class ChangeProjectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static ProjectionResult Project(string body, string? expectedCursor = null) =>
        ChangeProjector.Project(
            JsonSerializer.Deserialize<SyncChangesResponseDto>(body, WireJson.Options),
            Now,
            expectedCursor);

    private static string Page(string changes, string nextCursor = "c1", bool hasMore = false) =>
        $$"""
          {"changes":[{{changes}}],"nextCursor":"{{nextCursor}}","hasMore":{{(hasMore ? "true" : "false")}}}
          """;

    private static SyncProtocolException Reject(string body) =>
        Assert.Throws<SyncProtocolException>(() => Project(body));

    [Fact]
    public void An_upsert_is_projected_with_all_payload_fields()
    {
        var result = Project(Page(TaskJson.Change(
            Ids.One,
            "Read the contract",
            serverRevision: 7,
            completed: true,
            archivedAt: "2026-11-19T10:30:00Z",
            revision: 3)));

        var task = Assert.Single(result.Page.Tasks);
        Assert.Equal(Guid.Parse(Ids.One), task.Id);
        Assert.Equal("Read the contract", task.Title);
        Assert.Equal(string.Empty, task.Notes);
        Assert.True(task.IsCompleted);
        Assert.Equal(3, task.Revision);
        Assert.Equal(7, task.ServerRevision);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero), task.CreatedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero), task.UpdatedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 10, 30, 0, TimeSpan.Zero), task.ArchivedAtUtc);
        Assert.Equal(1, result.ChangeCount);
        Assert.Equal("c1", result.Page.NextCursor);
        Assert.False(result.Page.HasMore);
        Assert.Empty(result.Page.Tombstones);
    }

    [Fact]
    public void A_delete_needs_no_payload_and_uses_created_at()
    {
        var result = Project(Page(TaskJson.Change(Ids.One, serverRevision: 4, deleted: true)));

        Assert.Empty(result.Page.Tasks);
        var tombstone = Assert.Single(result.Page.Tombstones);
        Assert.Equal(Guid.Parse(Ids.One), tombstone.Id);
        Assert.Equal(4, tombstone.ServerRevision);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero), tombstone.DeletedAtUtc);
        Assert.Equal(1, result.ChangeCount);
    }

    [Fact]
    public void Setting_rows_are_skipped_without_failing_the_page()
    {
        var result = Project(Page(
            TaskJson.Setting("ui.language", "\"English\"", serverRevision: 1)
            + ","
            + TaskJson.Setting("ui.theme", "3", serverRevision: 2)));

        Assert.Empty(result.Page.Tasks);
        Assert.Empty(result.Page.Tombstones);
        Assert.Equal(0, result.ChangeCount);
        Assert.Equal("c1", result.Page.NextCursor);
    }

    [Fact]
    public void Deleted_false_without_a_payload_is_rejected()
    {
        var error = Reject(Page(
            $$"""{"serverRevision":1,"kind":"task","key":"{{Ids.One}}","deleted":false,"createdAt":"2026-11-19T09:00:00Z"}"""));

        Assert.Equal(WireErrorCodes.SyncInvalid, error.Code);
    }

    [Fact]
    public void Canonical_deleted_wins_over_a_legacy_operation()
    {
        var result = Project(Page(
            $$"""{"serverRevision":9,"kind":"task","key":"{{Ids.One}}","operation":"upsert","deleted":true,"createdAt":"2026-11-19T09:00:00Z"}"""));

        var tombstone = Assert.Single(result.Page.Tombstones);
        Assert.Equal(9, tombstone.ServerRevision);
        Assert.Empty(result.Page.Tasks);
    }

    [Fact]
    public void A_delete_wins_an_equal_revision_upsert_regardless_of_order()
    {
        var upsert = TaskJson.Change(Ids.One, "Keeps or not", serverRevision: 5);
        var deleted = TaskJson.Change(Ids.One, serverRevision: 5, deleted: true);

        var first = Project(Page(upsert + "," + deleted));
        var second = Project(Page(deleted + "," + upsert));

        foreach (var result in new[] { first, second })
        {
            Assert.Empty(result.Page.Tasks);
            Assert.Equal(5, Assert.Single(result.Page.Tombstones).ServerRevision);
            Assert.Equal(2, result.ChangeCount);
        }
    }

    [Fact]
    public void A_newer_upsert_removes_an_older_tombstone_on_the_same_page()
    {
        var result = Project(Page(
            TaskJson.Change(Ids.One, serverRevision: 5, deleted: true)
            + ","
            + TaskJson.Change(Ids.One, "Back again", serverRevision: 6)));

        Assert.Equal("Back again", Assert.Single(result.Page.Tasks).Title);
        Assert.Empty(result.Page.Tombstones);
    }

    [Fact]
    public void An_older_upsert_does_not_bury_a_newer_tombstone()
    {
        var result = Project(Page(
            TaskJson.Change(Ids.One, "Stale", serverRevision: 5)
            + ","
            + TaskJson.Change(Ids.One, serverRevision: 6, deleted: true)));

        Assert.Empty(result.Page.Tasks);
        Assert.Equal(6, Assert.Single(result.Page.Tombstones).ServerRevision);
    }

    [Fact]
    public void A_later_revision_wins_within_the_page()
    {
        var result = Project(Page(
            TaskJson.Change(Ids.One, "Old", serverRevision: 1)
            + ","
            + TaskJson.Change(Ids.One, "New", serverRevision: 2)
            + ","
            + TaskJson.Change(Ids.One, "Oldest", serverRevision: 1, revision: 1)));

        Assert.Equal("New", Assert.Single(result.Page.Tasks).Title);
        Assert.Equal(3, result.ChangeCount);
    }

    [Fact]
    public void An_identical_duplicate_at_the_same_revision_is_idempotent()
    {
        var change = TaskJson.Change(Ids.One, "Same", serverRevision: 3);
        var result = Project(Page(change + "," + change));

        Assert.Single(result.Page.Tasks);
        Assert.Equal(2, result.ChangeCount);
    }

    [Fact]
    public void Different_payloads_at_the_same_revision_are_a_conflict()
    {
        var error = Reject(Page(
            TaskJson.Change(Ids.One, "First", serverRevision: 3)
            + ","
            + TaskJson.Change(Ids.One, "Second", serverRevision: 3)));

        Assert.Equal(WireErrorCodes.SyncConflict, error.Code);
    }

    [Fact]
    public void An_unknown_kind_is_rejected()
    {
        var error = Reject(Page(
            $$"""{"serverRevision":1,"kind":"comment","key":"{{Ids.One}}","deleted":true,"createdAt":"2026-11-19T09:00:00Z"}"""));

        Assert.Equal(WireErrorCodes.SyncInvalid, error.Code);
    }

    [Fact]
    public void An_unknown_legacy_operation_is_rejected_not_guessed()
    {
        // Only the legacy shape can express this: a canonical `deleted` flag
        // always resolves to a known operation.
        var error = Reject(Page(
            $$"""{"serverRevision":1,"kind":"task","key":"{{Ids.One}}","operation":"merge","payload":{{TaskJson.Build(Ids.One)}}}"""));

        Assert.Equal(WireErrorCodes.SyncInvalid, error.Code);
    }

    [Fact]
    public void A_missing_server_revision_is_rejected()
    {
        var error = Reject(Page(
            $$"""{"kind":"task","key":"{{Ids.One}}","deleted":true,"createdAt":"2026-11-19T09:00:00Z"}"""));

        Assert.Equal(WireErrorCodes.SyncInvalid, error.Code);
    }

    [Fact]
    public void A_non_positive_server_revision_is_rejected()
    {
        var error = Reject(Page(TaskJson.Change(Ids.One, serverRevision: -1)));

        Assert.Equal(WireErrorCodes.SyncInvalid, error.Code);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void An_invalid_key_is_rejected(string key)
    {
        var error = Reject(Page(
            $$"""{"serverRevision":1,"kind":"task","key":"{{key}}","deleted":true,"createdAt":"2026-11-19T09:00:00Z"}"""));

        Assert.Equal(WireErrorCodes.SyncInvalid, error.Code);
    }

    [Fact]
    public void A_scalar_payload_is_rejected()
    {
        var error = Reject(Page(
            $$"""{"serverRevision":1,"kind":"task","key":"{{Ids.One}}","deleted":false,"createdAt":"2026-11-19T09:00:00Z","payload":"text"}"""));

        Assert.Equal(WireErrorCodes.SyncInvalid, error.Code);
    }

    [Fact]
    public void A_payload_whose_id_disagrees_with_the_key_is_rejected()
    {
        var error = Reject(Page(
            $$"""{"serverRevision":1,"kind":"task","key":"{{Ids.One}}","deleted":false,"createdAt":"2026-11-19T09:00:00Z","payload":{{TaskJson.Build(Ids.Two)}}}"""));

        Assert.Equal(WireErrorCodes.SyncInvalid, error.Code);
    }

    [Fact]
    public void A_payload_without_an_id_uses_the_change_key()
    {
        var result = Project(Page(
            $$"""{"serverRevision":1,"kind":"task","key":"{{Ids.One}}","deleted":false,"createdAt":"2026-11-19T09:00:00Z","payload":{"title":"No id","revision":1,"updatedAtUtc":"2026-11-19T09:00:00Z"} }"""));

        Assert.Equal(Guid.Parse(Ids.One), Assert.Single(result.Page.Tasks).Id);
    }

    [Fact]
    public void A_blank_title_is_rejected()
    {
        var error = Reject(Page(TaskJson.Change(Ids.One, "   ")));

        Assert.Equal(WireErrorCodes.SyncInvalid, error.Code);
    }

    [Fact]
    public void A_negative_payload_revision_is_rejected()
    {
        var error = Reject(Page(TaskJson.Change(Ids.One, revision: -1)));

        Assert.Equal(WireErrorCodes.SyncInvalid, error.Code);
    }

    [Fact]
    public void An_unparseable_payload_is_rejected()
    {
        var error = Reject(Page(
            $$"""{"serverRevision":1,"kind":"task","key":"{{Ids.One}}","deleted":false,"createdAt":"2026-11-19T09:00:00Z","payload":{"title":"Bad time","revision":1,"updatedAtUtc":"not-a-date"} }"""));

        Assert.Equal(WireErrorCodes.SyncInvalid, error.Code);
    }

    [Fact]
    public void Missing_timestamps_fall_back_to_the_change_and_sync_time()
    {
        var result = Project(Page(
            $$"""{"serverRevision":1,"kind":"task","key":"{{Ids.One}}","deleted":false,"payload":{"title":"No times","revision":0} }"""));

        var task = Assert.Single(result.Page.Tasks);
        Assert.Equal(Now, task.CreatedAtUtc);
        Assert.Equal(Now, task.UpdatedAtUtc);
        Assert.Equal(0, task.Revision);
    }

    [Fact]
    public void A_blank_next_cursor_is_rejected()
    {
        var error = Reject(Page(TaskJson.Change(Ids.One), nextCursor: " "));

        Assert.Equal(WireErrorCodes.SyncInvalid, error.Code);
    }

    [Fact]
    public void A_null_response_is_a_client_protocol_error()
    {
        var error = Assert.Throws<SyncProtocolException>(() =>
            ChangeProjector.Project(null, Now, null));

        Assert.Equal(LocalTransportCodes.Protocol, error.Code);
    }

    [Fact]
    public void An_empty_page_is_valid_and_counts_no_changes()
    {
        var result = Project(Page(string.Empty, nextCursor: "c9", hasMore: true), expectedCursor: "c8");

        Assert.Empty(result.Page.Tasks);
        Assert.Empty(result.Page.Tombstones);
        Assert.Equal(0, result.ChangeCount);
        Assert.True(result.Page.HasMore);
        Assert.Equal("c9", result.Page.NextCursor);
        Assert.Equal("c8", result.Page.ExpectedCursor);
        Assert.Equal(Now, result.Page.SyncedAt);
    }

    [Fact]
    public void A_device_id_is_carried_into_the_projection()
    {
        var result = Project(Page(
            $$"""{"serverRevision":1,"kind":"task","key":"{{Ids.One}}","deviceId":"phone-1","deleted":false,"createdAt":"2026-11-19T09:00:00Z","payload":{{TaskJson.Build(Ids.One)}}}"""));

        Assert.Equal("phone-1", Assert.Single(result.Page.Tasks).DeviceId);
    }
}
