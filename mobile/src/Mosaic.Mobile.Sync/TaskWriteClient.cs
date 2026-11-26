using System.Text.Json;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync;

/// <summary>A narrow, one-task command issued by the Android AppWidget.</summary>
public enum WidgetTaskAction
{
    Complete = 0,
    Archive,
    Restore,
}

/// <summary>How a single narrow write ended.</summary>
public enum TaskWriteOutcome
{
    Applied,
    Replayed,
    Conflict,
    SessionExpired,
    Offline,
    ServerError,
    RateLimited,
    Protocol,
    Cancelled,
}

/// <summary>The server's winning row from a 409 conflict response.</summary>
public sealed record TaskWriteConflict(
    string? Kind,
    string? Key,
    long ServerRevision,
    bool Deleted,
    JsonElement? Payload);

public sealed record TaskWriteResult(
    TaskWriteOutcome Outcome,
    SyncError? Error = null,
    string? Code = null,
    TaskWriteConflict? Current = null)
{
    public bool Succeeded => Outcome is TaskWriteOutcome.Applied or TaskWriteOutcome.Replayed;
}

/// <summary>
/// Sends one full <see cref="TaskItemDto"/> per action through the shared HTTP
/// stack. The server performs an opaque payload swap: completion and archiving
/// are client-side semantics; there is no single-task GET and no outbox here.
/// </summary>
public sealed class TaskWriteClient
{
    private readonly IMosaicApi _api;
    private readonly TimeProvider _time;

    public TaskWriteClient(IMosaicApi api, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        _time = time ?? TimeProvider.System;
    }

    public async Task<TaskWriteResult> WriteAsync(
        SyncedTask task,
        WidgetTaskAction action,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A device id is required.", nameof(deviceId));
        }

        var now = _time.GetUtcNow();
        var payload = BuildPayload(task, action, now);
        var body = new ChangePushRequestDto
        {
            DeviceId = deviceId,
            Kind = "task",
            Key = task.Id.ToString("D"),
            Payload = JsonSerializer.SerializeToElement(payload, WireJson.Options),
            Deleted = false,
            BaseServerRevision = task.ServerRevision,
            ClientRevision = task.Revision + 1,
            IdempotencyKey = Guid.NewGuid().ToString("D"),
        };

        ApiResult<SyncPushResponseDto> result;
        try
        {
            result = await _api.PostAsync<SyncPushResponseDto>(
                "v1/sync/changes",
                body,
                authenticated: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TaskWriteResult(TaskWriteOutcome.Cancelled);
        }

        if (result.IsSuccess)
        {
            return new TaskWriteResult(
                result.Value?.Replayed == true
                    ? TaskWriteOutcome.Replayed
                    : TaskWriteOutcome.Applied);
        }

        if (result.StatusCode == 409)
        {
            var current = result.Error?.DetailsAs<SyncConflictDetailsDto>()?.Current;
            return new TaskWriteResult(
                TaskWriteOutcome.Conflict,
                Code: result.ErrorCode,
                Current: current is null
                    ? null
                    : new TaskWriteConflict(
                        current.Kind,
                        current.Key,
                        current.ServerRevision,
                        current.Deleted,
                        current.Payload));
        }

        if (result.Error?.Code is LocalTransportCodes.Protocol or LocalTransportCodes.Cancelled)
        {
            return new TaskWriteResult(
                result.Error.Code == LocalTransportCodes.Protocol
                    ? TaskWriteOutcome.Protocol
                    : TaskWriteOutcome.Cancelled,
                Code: result.Error.Code);
        }

        var error = SyncErrorMapper.FromApi(result.StatusCode, result.Error);
        var outcome = error.Kind switch
        {
            SyncErrorKind.SessionExpired => TaskWriteOutcome.SessionExpired,
            SyncErrorKind.Offline => TaskWriteOutcome.Offline,
            SyncErrorKind.RateLimited => TaskWriteOutcome.RateLimited,
            SyncErrorKind.ServerError => TaskWriteOutcome.ServerError,
            SyncErrorKind.Protocol => TaskWriteOutcome.Protocol,
            SyncErrorKind.Cancelled => TaskWriteOutcome.Cancelled,
            _ => TaskWriteOutcome.Protocol,
        };

        return new TaskWriteResult(outcome, error, error.Code);
    }

    internal static TaskItemDto BuildPayload(SyncedTask task, WidgetTaskAction action, DateTimeOffset now)
    {
        var completed = task.IsCompleted;
        var completedAt = task.CompletedAtUtc;
        var archivedAt = task.ArchivedAtUtc;
        var restoredAt = task.RestoredAtUtc;

        switch (action)
        {
            case WidgetTaskAction.Complete:
                completed = true;
                completedAt = now;
                restoredAt = null;
                break;
            case WidgetTaskAction.Archive:
                archivedAt = now;
                break;
            case WidgetTaskAction.Restore:
                restoredAt = now;
                archivedAt = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown widget task action.");
        }

        return new TaskItemDto
        {
            Id = task.Id,
            Title = task.Title,
            Notes = task.Notes,
            IsCompleted = completed,
            CreatedAtUtc = task.CreatedAtUtc,
            UpdatedAtUtc = now,
            Revision = task.Revision + 1,
            CompletedAtUtc = completedAt,
            ArchivedAtUtc = archivedAt,
            RestoredAtUtc = restoredAt,
        };
    }
}
