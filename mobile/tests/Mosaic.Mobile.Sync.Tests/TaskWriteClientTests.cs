using System.Net;
using System.Text.Json;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync.Tests;

public sealed class TaskWriteClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    private static SyncedTask SampleTask() => new()
    {
        Id = Guid.Parse(Ids.One),
        Title = "Widget task",
        Notes = "notes",
        IsCompleted = false,
        CreatedAtUtc = Now.AddDays(-1),
        UpdatedAtUtc = Now.AddHours(-2),
        Revision = 7,
        CompletedAtUtc = null,
        ArchivedAtUtc = null,
        RestoredAtUtc = null,
        ServerRevision = 42,
    };

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubTokenProvider : IAccessTokenProvider
    {
        public string? AccessToken => "test-token";

        public Task<string?> RefreshAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(AccessToken);
    }

    private sealed class StubApi : IMosaicApi
    {
        public Func<ApiResult<SyncPushResponseDto>> Responder { get; set; } =
            () => new() { StatusCode = 200 };

        public RecordedRequest? Request { get; private set; }

        public Task<ApiResult<T>> GetAsync<T>(string relativePath, bool authenticated = true,
            bool retryTransient = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApiResult<T>> PostAsync<T>(string relativePath, object? body,
            bool authenticated = true, bool retryTransient = false,
            CancellationToken cancellationToken = default)
        {
            Request = new RecordedRequest(
                HttpMethod.Post,
                relativePath,
                JsonSerializer.Serialize(body, WireJson.Options),
                authenticated ? "Bearer test-token" : null);
            return Task.FromResult((ApiResult<T>)(object)Responder());
        }

        public Task<ApiResult<byte[]>> GetBytesAsync(string relativePath, long maxBytes,
            bool authenticated = true, bool retryTransient = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static (TaskWriteClient Client, StubApi Api) Create(
        Func<ApiResult<SyncPushResponseDto>> responder)
    {
        var api = new StubApi { Responder = responder };
        return (new TaskWriteClient(api, new FixedTimeProvider()), api);
    }

    private static (TaskWriteClient Client, StubHandler Handler) Create(
        Func<RecordedRequest, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var api = new MosaicApiClient(new HttpClient(handler), (_, _) => Task.CompletedTask)
        {
            BaseUrl = "https://example.test",
            TokenProvider = new StubTokenProvider(),
        };
        return (new TaskWriteClient(api, new FixedTimeProvider()), handler);
    }

    private static ChangePushRequestDto Body(RecordedRequest request) =>
        JsonSerializer.Deserialize<ChangePushRequestDto>(
            request.Body ?? "{}",
            WireJson.Options)!;

    private static TaskItemDto WrappedPayload(RecordedRequest request) =>
        JsonSerializer.Deserialize<TaskItemDto>(
            JsonSerializer.Deserialize<JsonElement>(request.Body ?? "{}").GetProperty("payload"),
            WireJson.Options)!;

    private static HttpResponseMessage Ok(bool replayed = false) =>
        Responses.Json(
            HttpStatusCode.OK,
            WireJson.Serialize(new SyncPushResponseDto { Replayed = replayed }));

    [Fact]
    public async Task Complete_writes_full_payload_without_tombstone()
    {
        var (client, handler) = Create(_ => Ok());

        var result = await client.WriteAsync(SampleTask(), WidgetTaskAction.Complete, "device-1");

        Assert.Equal(TaskWriteOutcome.Applied, result.Outcome);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/v1/sync/changes", request.Uri, StringComparison.Ordinal);

        var body = Body(request);
        Assert.Equal("task", body.Kind);
        Assert.Equal(Ids.One, body.Key);
        Assert.Equal("device-1", body.DeviceId);
        Assert.False(body.Deleted);
        Assert.Equal(42, body.BaseServerRevision);
        Assert.Equal(8, body.ClientRevision);
        Assert.True(Guid.TryParse(body.IdempotencyKey, out _));

        var payload = WrappedPayload(request);
        Assert.Equal(Ids.One, payload.Id?.ToString("D"));
        Assert.Equal("Widget task", payload.Title);
        Assert.Equal("notes", payload.Notes);
        Assert.True(payload.IsCompleted);
        Assert.Equal(Now, payload.CompletedAtUtc);
        Assert.Null(payload.RestoredAtUtc);
        Assert.Null(payload.ArchivedAtUtc);
        Assert.Equal(8, payload.Revision);
        Assert.Equal(Now, payload.UpdatedAtUtc);
        Assert.NotNull(request.Authorization);
    }

    [Fact]
    public async Task Archive_keeps_completed_state_and_never_sets_deleted()
    {
        var task = SampleTask() with { IsCompleted = true, CompletedAtUtc = Now.AddHours(-1) };
        var (client, handler) = Create(_ => Ok());

        await client.WriteAsync(task, WidgetTaskAction.Archive, "device-1");

        var request = Assert.Single(handler.Requests);
        Assert.False(Body(request).Deleted);
        var payload = WrappedPayload(request);
        Assert.True(payload.IsCompleted);
        Assert.Equal(task.CompletedAtUtc, payload.CompletedAtUtc);
        Assert.Equal(Now, payload.ArchivedAtUtc);
        Assert.Null(payload.RestoredAtUtc);
    }

    [Fact]
    public async Task Restore_clears_archive_and_preserves_completion()
    {
        var task = SampleTask() with
        {
            IsCompleted = true,
            CompletedAtUtc = Now.AddHours(-1),
            ArchivedAtUtc = Now.AddHours(-1),
        };
        var (client, handler) = Create(_ => Ok());

        await client.WriteAsync(task, WidgetTaskAction.Restore, "device-1");

        var payload = WrappedPayload(Assert.Single(handler.Requests));
        Assert.True(payload.IsCompleted);
        Assert.Equal(task.CompletedAtUtc, payload.CompletedAtUtc);
        Assert.Null(payload.ArchivedAtUtc);
        Assert.Equal(Now, payload.RestoredAtUtc);
    }

    [Fact]
    public async Task Each_action_generates_a_new_idempotency_key()
    {
        var (client, handler) = Create(_ => Ok());
        var task = SampleTask();

        await client.WriteAsync(task, WidgetTaskAction.Complete, "device-1");
        await client.WriteAsync(task with { Revision = 8 }, WidgetTaskAction.Archive, "device-1");

        Assert.Equal(2, handler.Requests.Count);
        Assert.NotEqual(
            Body(handler.Requests[0]).IdempotencyKey,
            Body(handler.Requests[1]).IdempotencyKey);
    }

    [Fact]
    public async Task Conflict_parses_current_from_details()
    {
        var (client, _) = Create(_ => Responses.Error(
            HttpStatusCode.Conflict,
            "sync.conflict",
            new
            {
                current = new
                {
                    kind = "task",
                    key = Ids.One,
                    serverRevision = 77,
                    deleted = false,
                    payload = SampleTask() with { Title = "Other device" },
                },
            }));

        var result = await client.WriteAsync(SampleTask(), WidgetTaskAction.Complete, "device-1");

        Assert.Equal(TaskWriteOutcome.Conflict, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal("sync.conflict", result.Code);
        Assert.NotNull(result.Current);
        Assert.Equal(77, result.Current.ServerRevision);
    }

    [Fact]
    public async Task Missing_conflict_details_are_tolerated()
    {
        var (client, _) = Create(_ => Responses.Error(HttpStatusCode.Conflict, "sync.conflict"));

        var result = await client.WriteAsync(SampleTask(), WidgetTaskAction.Complete, "device-1");

        Assert.Equal(TaskWriteOutcome.Conflict, result.Outcome);
        Assert.Null(result.Current);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "auth.session_expired", TaskWriteOutcome.SessionExpired)]
    [InlineData(HttpStatusCode.ServiceUnavailable, WireErrorCodes.ServerUnavailable, TaskWriteOutcome.ServerError)]
    [InlineData(HttpStatusCode.TooManyRequests, "auth.rate_limited", TaskWriteOutcome.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, "client.protocol", TaskWriteOutcome.Protocol)]
    public async Task Http_failures_map_to_explicit_outcomes(
        HttpStatusCode status,
        string code,
        TaskWriteOutcome expected)
    {
        var (client, _) = Create(_ => Responses.Error(status, code));

        var result = await client.WriteAsync(SampleTask(), WidgetTaskAction.Complete, "device-1");

        Assert.Equal(expected, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Code);
    }

    [Theory]
    [InlineData(LocalTransportCodes.Network)]
    [InlineData(LocalTransportCodes.Timeout)]
    public async Task Transport_failures_map_to_offline(string code)
    {
        var (client, _) = Create(() => ApiResult<SyncPushResponseDto>.Fail(
            0,
            new WireErrorDto { Code = code }));

        var result = await client.WriteAsync(SampleTask(), WidgetTaskAction.Complete, "device-1");

        Assert.Equal(TaskWriteOutcome.Offline, result.Outcome);
        Assert.Equal(code, result.Code);
    }

    [Fact]
    public async Task Protocol_failures_keep_explicit_outcome()
    {
        var (client, _) = Create(() => ApiResult<SyncPushResponseDto>.Fail(
            0,
            new WireErrorDto { Code = LocalTransportCodes.Protocol }));

        var result = await client.WriteAsync(SampleTask(), WidgetTaskAction.Complete, "device-1");

        Assert.Equal(TaskWriteOutcome.Protocol, result.Outcome);
        Assert.Equal(LocalTransportCodes.Protocol, result.Code);
    }

    [Fact]
    public async Task Replayed_response_is_success()
    {
        var (client, handler) = Create(_ => Ok(replayed: true));

        var result = await client.WriteAsync(SampleTask(), WidgetTaskAction.Complete, "device-1");

        Assert.Equal(TaskWriteOutcome.Replayed, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Single(handler.Requests);
    }
}
