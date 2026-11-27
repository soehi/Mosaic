using System.Globalization;
using System.Text.Json;
using Mosaic.Mobile.Core.Cards;
using Mosaic.Mobile.Core.Commands;
using Mosaic.Mobile.Core.Projection;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Tests;

public sealed class TaskCommandTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Complete_sets_only_the_named_fields_and_keeps_unknown_members()
    {
        await using var projection = Seeded(isCompleted: false, revision: 2);
        var api = new FakeMosaicApi
        {
            Responder = (_, _, body) => FakeMosaicApi.Applied((ChangePushRequestDto)body!),
        };
        var tokens = new RecordingTokenSource();
        var service = new TaskCommandService(tokens);

        var result = await service.ExecuteAsync(api, projection, Sample.TaskId, TaskAction.Complete, "device-1", CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        var request = Assert.Single(api.Requests);
        Assert.Equal("v1/sync/changes", request.Path);
        var body = Assert.IsType<ChangePushRequestDto>(request.Body);
        Assert.Equal("device-1", body.DeviceId);
        Assert.Equal(SyncKinds.Task, body.Kind);
        Assert.Equal(Sample.TaskId.ToString("D"), body.Key);
        Assert.False(body.Deleted);
        Assert.Equal(0, body.BaseServerRevision);
        Assert.Equal(3, body.ClientRevision);
        Assert.False(string.IsNullOrWhiteSpace(body.IdempotencyKey));
        Assert.Equal(32, body.IdempotencyKey.Length);

        var payload = body.Payload!.Value;
        Assert.True(payload.GetProperty("isCompleted").GetBoolean());
        Assert.False(payload.GetProperty("completedAtUtc").ValueKind == JsonValueKind.Null);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("restoredAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("archivedAtUtc").ValueKind);
        Assert.Equal(3, payload.GetProperty("revision").GetInt64());
        Assert.Equal("不应出现在卡片上的备注", payload.GetProperty("notes").GetString());
        Assert.Equal(2, payload.GetProperty("attachmentCount").GetInt32());

        var stored = await projection.FindAsync(Sample.TaskId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.True(stored!.IsCompleted);
        Assert.Equal(3, stored.Revision);
        Assert.Equal(1, stored.ServerRevision);
    }

    [Fact]
    public async Task Archive_writes_archived_at_and_never_a_tombstone()
    {
        var payload = Sample.Payload(
            Sample.TaskId,
            revision: 4,
            isCompleted: true,
            completedAt: DateTimeOffset.Parse("2026-11-24T00:00:00Z", CultureInfo.InvariantCulture));
        await using var projection = Seeded(task: Sample.Task(Sample.TaskId, payload, serverRevision: 6));
        var api = new FakeMosaicApi
        {
            Responder = (_, _, body) => FakeMosaicApi.Applied((ChangePushRequestDto)body!),
        };
        var service = new TaskCommandService(new RecordingTokenSource());

        var result = await service.ExecuteAsync(api, projection, Sample.TaskId, TaskAction.Archive, "device-1", CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        var body = Assert.IsType<ChangePushRequestDto>(Assert.Single(api.Requests).Body);
        Assert.False(body.Deleted);
        Assert.Equal(6, body.BaseServerRevision);
        Assert.Equal(5, body.ClientRevision);
        var value = body.Payload!.Value;
        Assert.NotEqual(JsonValueKind.Null, value.GetProperty("archivedAtUtc").ValueKind);
        Assert.True(value.GetProperty("isCompleted").GetBoolean());
        Assert.Equal("2026-11-24T00:00:00Z", value.GetProperty("completedAtUtc").GetString());
        Assert.Equal(5, value.GetProperty("revision").GetInt64());

        var stored = await projection.FindAsync(Sample.TaskId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.True(stored!.IsArchived);
        var visible = await projection.ListVisibleAsync(5, CancellationToken.None);
        Assert.Empty(visible);
    }

    [Fact]
    public async Task Conflict_applies_the_server_current_row()
    {
        await using var projection = Seeded();
        var serverPayload = Sample.Payload(Sample.TaskId, revision: 9, title: "服务器标题");
        VarDetails(out var details, Sample.TaskId, serverPayload, serverRevision: 9);
        var api = new FakeMosaicApi
        {
            Responder = (_, _, _) => FakeMosaicApi.Failure<ChangePushResponseDto>(409, WireErrorCodes.SyncConflict, details),
        };
        var service = new TaskCommandService(new RecordingTokenSource());

        var result = await service.ExecuteAsync(api, projection, Sample.TaskId, TaskAction.Complete, "device-1", CancellationToken.None);

        Assert.Equal(CommandOutcome.Conflict, result.Outcome);
        var stored = await projection.FindAsync(Sample.TaskId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal("服务器标题", stored!.Title);
        Assert.Equal(9, stored.Revision);
        Assert.Equal(9, stored.ServerRevision);
    }

    [Fact]
    public async Task Replayed_response_is_a_success_without_a_second_revision_bump()
    {
        await using var projection = Seeded();
        ChangePushRequestDto? sent = null;
        var api = new FakeMosaicApi
        {
            Responder = (_, _, body) =>
            {
                sent = (ChangePushRequestDto)body!;
                return FakeMosaicApi.Applied(sent, replayed: true);
            },
        };
        var service = new TaskCommandService(new RecordingTokenSource());

        var result = await service.ExecuteAsync(api, projection, Sample.TaskId, TaskAction.Complete, "device-1", CancellationToken.None);

        Assert.Equal(CommandOutcome.Replayed, result.Outcome);
        Assert.Equal(3, sent!.ClientRevision);
        var stored = await projection.FindAsync(Sample.TaskId, CancellationToken.None);
        Assert.Equal(3, stored!.Revision);
        Assert.Equal(1, stored.ServerRevision);
    }

    [Fact]
    public async Task Unauthorized_clears_the_credential()
    {
        await using var projection = Seeded();
        var tokens = new RecordingTokenSource();
        var api = new FakeMosaicApi
        {
            Responder = (_, _, _) => FakeMosaicApi.Failure<ChangePushResponseDto>(401, WireErrorCodes.AuthRequired),
        };
        var service = new TaskCommandService(tokens);

        var result = await service.ExecuteAsync(api, projection, Sample.TaskId, TaskAction.Complete, "device-1", CancellationToken.None);

        Assert.Equal(CommandOutcome.SessionExpired, result.Outcome);
        Assert.True(tokens.Invalidated);
        var stored = await projection.FindAsync(Sample.TaskId, CancellationToken.None);
        Assert.False(stored!.IsCompleted);
    }

    [Fact]
    public async Task Transport_failure_is_offline_and_leaves_the_projection_untouched()
    {
        await using var projection = Seeded();
        var api = new FakeMosaicApi
        {
            Responder = (_, _, _) => FakeMosaicApi.Transport<ChangePushResponseDto>(),
        };
        var service = new TaskCommandService(new RecordingTokenSource());

        var result = await service.ExecuteAsync(api, projection, Sample.TaskId, TaskAction.Archive, "device-1", CancellationToken.None);

        Assert.Equal(CommandOutcome.Offline, result.Outcome);
        Assert.Equal("离线，操作未提交", result.Message);
        var stored = await projection.FindAsync(Sample.TaskId, CancellationToken.None);
        Assert.False(stored!.IsArchived);
        Assert.Equal(2, stored.Revision);
    }

    [Fact]
    public async Task Rapid_double_tap_posts_once_and_then_reports_already_applied()
    {
        await using var projection = Seeded();
        var api = new FakeMosaicApi
        {
            Responder = (_, _, body) => FakeMosaicApi.Applied((ChangePushRequestDto)body!),
        };
        var service = new TaskCommandService(new RecordingTokenSource());

        var first = await service.ExecuteAsync(api, projection, Sample.TaskId, TaskAction.Complete, "device-1", CancellationToken.None);
        var second = await service.ExecuteAsync(api, projection, Sample.TaskId, TaskAction.Complete, "device-1", CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, first.Outcome);
        Assert.Equal(CommandOutcome.AlreadyApplied, second.Outcome);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task Idempotency_keys_differ_between_commands()
    {
        await using var projection = Seeded();
        var keys = new List<string>();
        var api = new FakeMosaicApi
        {
            Responder = (_, _, body) =>
            {
                var request = (ChangePushRequestDto)body!;
                keys.Add(request.IdempotencyKey);
                return FakeMosaicApi.Applied(request);
            },
        };
        var service = new TaskCommandService(new RecordingTokenSource());

        await service.ExecuteAsync(api, projection, Sample.TaskId, TaskAction.Complete, "device-1", CancellationToken.None);
        await service.ExecuteAsync(api, projection, Sample.TaskId, TaskAction.Archive, "device-1", CancellationToken.None);

        Assert.Equal(2, keys.Count);
        Assert.NotEqual(keys[0], keys[1]);
    }

    private TaskProjection Seeded(bool isCompleted = false, long revision = 2, SyncedTask? task = null) =>
        Sample.SeededProjection(
            _directory.Path,
            ProjectionIdentity.Create("https://mosaic.test/", "user-1"),
            task ?? Sample.Task(Sample.TaskId, Sample.Payload(Sample.TaskId, revision, isCompleted: isCompleted)));

    private static void VarDetails(out JsonElement details, Guid id, string payload, long serverRevision)
    {
        var json = "{\"current\":{\"kind\":\"task\",\"key\":\""
            + id.ToString("D", CultureInfo.InvariantCulture)
            + "\",\"serverRevision\":"
            + serverRevision.ToString(CultureInfo.InvariantCulture)
            + ",\"deleted\":false,\"payload\":"
            + payload
            + "}}";
        using var document = JsonDocument.Parse(json);
        details = document.RootElement.Clone();
    }
}