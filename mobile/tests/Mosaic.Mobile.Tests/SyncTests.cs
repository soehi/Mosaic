using System.Text.Json;
using Mosaic.Mobile.Core.Projection;
using Mosaic.Mobile.Core.Sync;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Bootstrap_restores_tasks_and_does_not_stamp_the_snapshot_revision()
    {
        var items = new[] { Sample.BackupItem(Sample.TaskId, Sample.Payload(Sample.TaskId)) };
        var snapshot = Sample.Snapshot(items);
        var manifest = Sample.Manifest(snapshot);

        var api = new FakeMosaicApi
        {
            Responder = (method, _, _) => method switch
            {
                "GET" => FakeMosaicApi.Ok(manifest),
                "GET-BYTES" => FakeMosaicApi.Ok(JsonSerializer.SerializeToUtf8Bytes(snapshot)),
                _ => throw new InvalidOperationException(method),
            },
        };

        var projection = await OpenAsync();
        await using (projection)
        {
            var result = await new SnapshotBootstrapService().RunAsync(api, projection, "token", CancellationToken.None);

            Assert.True(result.IsRestored, result.ErrorCode);
            Assert.Equal(1, result.TaskCount);
            var stored = await projection.FindAsync(Sample.TaskId, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal(0, stored!.ServerRevision);
            Assert.Equal(2, stored.Revision);
            Assert.NotNull(projection.Baseline);
            Assert.Equal("snapshot-1", projection.Baseline!.SnapshotId);
            Assert.Equal("7", projection.Cursor);
        }
    }

    [Fact]
    public async Task Bootstrap_rejects_a_snapshot_whose_digest_does_not_match()
    {
        var items = new[] { Sample.BackupItem(Sample.TaskId, Sample.Payload(Sample.TaskId)) };
        var snapshot = Sample.Snapshot(items);
        var manifest = Sample.Manifest(snapshot) with { ContentSha256 = new string('a', 64) };

        var api = new FakeMosaicApi
        {
            Responder = (method, _, _) => method switch
            {
                "GET" => FakeMosaicApi.Ok(manifest),
                "GET-BYTES" => FakeMosaicApi.Ok(JsonSerializer.SerializeToUtf8Bytes(snapshot)),
                _ => throw new InvalidOperationException(method),
            },
        };

        var projection = await OpenAsync();
        await using (projection)
        {
            var result = await new SnapshotBootstrapService().RunAsync(api, projection, "token", CancellationToken.None);

            Assert.Equal(BootstrapOutcome.Corrupt, result.Outcome);
            Assert.Equal(WireErrorCodes.BackupHashMismatch, result.ErrorCode);
            Assert.Null(await projection.FindAsync(Sample.TaskId, CancellationToken.None));
            Assert.Empty(await projection.ListVisibleAsync(5, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Pull_walks_pages_and_stops_at_the_head()
    {
        var projection = await OpenAsync();
        await using (projection)
        {
            await projection.ReplaceAsync(
                [Sample.Task(Sample.TaskId, Sample.Payload(Sample.TaskId))],
                new ProjectionBaseline("snapshot-1", 42, DateTimeOffset.UtcNow, "7", DateTimeOffset.UtcNow),
                CancellationToken.None);

            var pageOne = new SyncChangePageDto
            {
                Changes =
                [
                    new SyncChangeDto
                    {
                        Kind = SyncKinds.Task,
                        Key = Sample.TaskId.ToString("D"),
                        ServerRevision = 43,
                        Payload = JsonDocument.Parse(Sample.Payload(Sample.TaskId, revision: 5, title: "第一页")).RootElement.Clone(),
                        Cursor = "8",
                    },
                ],
                NextCursor = "8",
                HasMore = true,
            };
            var pageTwo = new SyncChangePageDto
            {
                Changes =
                [
                    new SyncChangeDto
                    {
                        Kind = SyncKinds.Task,
                        Key = Sample.TaskId.ToString("D"),
                        ServerRevision = 44,
                        Payload = JsonDocument.Parse(Sample.Payload(Sample.TaskId, revision: 6, title: "第二页")).RootElement.Clone(),
                        Cursor = "9",
                    },
                ],
                NextCursor = "9",
                HasMore = false,
            };

            var api = new FakeMosaicApi
            {
                Responder = (_, path, _) => path.Contains("cursor=7", StringComparison.Ordinal)
                    ? FakeMosaicApi.Ok(pageOne)
                    : FakeMosaicApi.Ok(pageTwo),
            };

            var result = await new IncrementalSyncService().RunAsync(api, projection, "token", CancellationToken.None);

            Assert.True(result.IsCompleted, result.ErrorCode);
            Assert.Equal(2, result.Applied);
            var stored = await projection.FindAsync(Sample.TaskId, CancellationToken.None);
            Assert.Equal("第二页", stored!.Title);
            Assert.Equal(44, stored.ServerRevision);
            Assert.Equal("9", projection.Cursor);
        }
    }

    [Fact]
    public async Task Pull_reports_needs_snapshot_when_the_server_asks_for_it()
    {
        var projection = await OpenAsync();
        await using (projection)
        {
            await projection.ReplaceAsync(
                [],
                new ProjectionBaseline("snapshot-1", 42, DateTimeOffset.UtcNow, "7", DateTimeOffset.UtcNow),
                CancellationToken.None);

            var details = Sample.Details("""{"requiresSnapshot":true}""");
            var api = new FakeMosaicApi
            {
                Responder = (_, _, _) => FakeMosaicApi.Failure<SyncChangePageDto>(400, WireErrorCodes.SyncInvalid, details),
            };

            var result = await new IncrementalSyncService().RunAsync(api, projection, "token", CancellationToken.None);

            Assert.Equal(PullOutcome.NeedsSnapshot, result.Outcome);
        }
    }

    private Task<TaskProjection> OpenAsync()
    {
        var open = TaskProjection.OpenAsync(ProjectionIdentity.Create("https://mosaic.test/", "user-1"), _directory.Path, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(open.IsOpened, open.Detail);
        return Task.FromResult(open.Projection!);
    }
}