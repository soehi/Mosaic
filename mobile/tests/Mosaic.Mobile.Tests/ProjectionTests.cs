using System.Globalization;
using System.Text.Json;
using Mosaic.Mobile.Core.Projection;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Tests;

public sealed class ProjectionTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Projection_identity_is_stable_and_never_contains_the_account()
    {
        var first = ProjectionIdentity.Create("https://mosaic.test/", "user-1");
        var second = ProjectionIdentity.Create("https://mosaic.test/", "user-1");
        var otherUser = ProjectionIdentity.Create("https://mosaic.test/", "user-2");
        var otherServer = ProjectionIdentity.Create("https://other.test/", "user-1");

        Assert.Equal(first.OwnerKey, second.OwnerKey);
        Assert.NotEqual(first.OwnerKey, otherUser.OwnerKey);
        Assert.NotEqual(first.OwnerKey, otherServer.OwnerKey);
        Assert.Equal(64, first.OwnerKey.Length);
        Assert.DoesNotContain("user-1", first.OwnerKey, StringComparison.Ordinal);
        Assert.DoesNotContain("mosaic", first.OwnerKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_another_account_file_is_refused_and_quarantined()
    {
        var first = ProjectionIdentity.Create("https://mosaic.test/", "user-1");
        var open = await TaskProjection.OpenAsync(first, _directory.Path, CancellationToken.None);
        Assert.True(open.IsOpened);
        await open.Projection!.DisposeAsync();

        var second = ProjectionIdentity.Create("https://mosaic.test/", "user-2");
        var directory = Path.Combine(_directory.Path, "projections-v3");
        var source = Path.Combine(directory, first.OwnerKey + ".db");
        File.Copy(source, Path.Combine(directory, second.OwnerKey + ".db"), overwrite: true);

        var reopened = await TaskProjection.OpenAsync(second, _directory.Path, CancellationToken.None);
        Assert.False(reopened.IsOpened);
        Assert.Equal(ProjectionOpenStatus.OwnerMismatch, reopened.Status);
        Assert.True(Directory.Exists(Path.Combine(directory, "quarantine")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory, "quarantine")));
    }

    [Fact]
    public async Task A_corrupt_file_is_quarantined_instead_of_displayed()
    {
        var identity = ProjectionIdentity.Create("https://mosaic.test/", "user-1");
        var directory = Path.Combine(_directory.Path, "projections-v3");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, identity.OwnerKey + ".db"), "this is not sqlite");

        var open = await TaskProjection.OpenAsync(identity, _directory.Path, CancellationToken.None);

        Assert.Equal(ProjectionOpenStatus.Corrupt, open.Status);
    }

    [Fact]
    public async Task Visible_rows_exclude_archived_tasks_and_counts_split_correctly()
    {
        var archivedPayload = Sample.Payload(Sample.TaskId, revision: 2, isCompleted: true, archivedAt: DateTimeOffset.Parse("2026-11-25T00:00:00Z", CultureInfo.InvariantCulture));
        var active = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var completed = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var projection = Sample.SeededProjection(
            _directory.Path,
            ProjectionIdentity.Create("https://mosaic.test/", "user-1"),
            Sample.Task(Sample.TaskId, archivedPayload),
            Sample.Task(active, Sample.Payload(active, title: "活动任务")),
            Sample.Task(completed, Sample.Payload(completed, isCompleted: true, title: "已完成任务")));

        await using (projection)
        {
            var rows = await projection.ListVisibleAsync(5, CancellationToken.None);
            Assert.Equal(2, rows.Count);
            Assert.DoesNotContain(rows, row => row.Id == Sample.TaskId);
            Assert.False(rows[0].IsCompleted);

            var counts = await projection.CountAsync(CancellationToken.None);
            Assert.Equal(2, counts.Visible);
            Assert.Equal(1, counts.Open);
            Assert.Equal(1, counts.Archived);
        }
    }

    [Fact]
    public async Task Changes_apply_task_updates_tombstones_and_ignore_settings()
    {
        var projection = Sample.SeededProjection(
            _directory.Path,
            ProjectionIdentity.Create("https://mosaic.test/", "user-1"),
            Sample.Task(Sample.TaskId, Sample.Payload(Sample.TaskId)));
        await using (projection)
        {
            var updated = Sample.Payload(Sample.TaskId, revision: 4, title: "更新后的标题");
            var changes = new[]
            {
                new SyncChangeDto { Kind = SyncKinds.Task, Key = Sample.TaskId.ToString("D"), ServerRevision = 11, Payload = JsonDocument.Parse(updated).RootElement.Clone(), Cursor = "11" },
                new SyncChangeDto { Kind = SyncKinds.Setting, Key = "theme", ServerRevision = 12, Payload = JsonDocument.Parse("""{"value":"dark"}""").RootElement.Clone(), Cursor = "12" },
            };

            await projection.ApplyChangesAsync(changes, "12", CancellationToken.None);

            var stored = await projection.FindAsync(Sample.TaskId, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal("更新后的标题", stored!.Title);
            Assert.Equal(11, stored.ServerRevision);
            Assert.Equal("12", projection.Cursor);

            await projection.ApplyChangesAsync(
                [new SyncChangeDto { Kind = SyncKinds.Task, Key = Sample.TaskId.ToString("D"), ServerRevision = 13, Deleted = true, Cursor = "13" }],
                "13",
                CancellationToken.None);

            Assert.Null(await projection.FindAsync(Sample.TaskId, CancellationToken.None));
            Assert.Empty(await projection.ListVisibleAsync(5, CancellationToken.None));
        }
    }
}