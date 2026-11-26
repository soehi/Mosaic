using System.Globalization;
using System.Text.Json;
using Mosaic.Mobile.Core.Api;
using Mosaic.Mobile.Core.Auth;
using Mosaic.Mobile.Core.Cards;
using Mosaic.Mobile.Core.Projection;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Core.Commands;

public enum CommandOutcome
{
    Applied = 0,
    Replayed,
    AlreadyApplied,
    Conflict,
    NotFound,
    NotReady,
    SessionExpired,
    Offline,
    RateLimited,
    ServerError,
    Protocol,
    Cancelled,
}

public sealed record CommandResult(CommandOutcome Outcome, string Message)
{
    public bool IsSuccess => Outcome is CommandOutcome.Applied or CommandOutcome.Replayed or CommandOutcome.AlreadyApplied;
}

/// <summary>
/// The single write path: one task, one <c>POST /v1/sync/changes</c>, carrying
/// <c>baseServerRevision</c>, <c>clientRevision</c> and a fresh
/// <c>idempotencyKey</c>. There is no outbox and no optimistic local edit: the
/// projection only changes from the server's authoritative reply.
///
/// Failures are surfaced as state, never as a retry queue:
/// <list type="bullet">
/// <item>409 → conflict; the server's current row replaces the local row.</item>
/// <item>401 → tokens cleared, session expired.</item>
/// <item>transport failure → offline, projection untouched.</item>
/// </list>
/// </summary>
public sealed class TaskCommandService
{
    private readonly IAccessTokenSource _tokens;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TaskCommandService(IAccessTokenSource tokens, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _tokens = tokens;
        _time = time ?? TimeProvider.System;
    }

    public async Task<CommandResult> ExecuteAsync(
        IMosaicApi api,
        TaskProjection projection,
        Guid taskId,
        TaskAction action,
        string deviceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        // Serializes every write in the process: a double tap cannot start two
        // requests, and the second pass re-reads the projection under this gate.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var task = await projection.FindAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (task is null)
            {
                return new CommandResult(CommandOutcome.NotFound, "任务不在本地投影中，请刷新");
            }

            switch (action)
            {
                case TaskAction.Complete when task.IsCompleted:
                    return new CommandResult(CommandOutcome.AlreadyApplied, "任务已完成");
                case TaskAction.Archive when task.IsArchived:
                    return new CommandResult(CommandOutcome.AlreadyApplied, "任务已归档");
            }

            var accessToken = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(accessToken))
            {
                return new CommandResult(CommandOutcome.SessionExpired, "会话已失效，请重新登录");
            }

            var now = _time.GetUtcNow();
            var payloadJson = action switch
            {
                TaskAction.Complete => TaskPayload.SetComplete(task.PayloadJson, now),
                TaskAction.Archive => TaskPayload.SetArchived(task.PayloadJson, now),
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };

            var clientRevision = TaskPayload.ReadRevision(payloadJson);
            var body = new ChangePushRequestDto
            {
                DeviceId = deviceId,
                Kind = SyncKinds.Task,
                Key = taskId.ToString("D", CultureInfo.InvariantCulture),
                Payload = ParsePayload(payloadJson),
                Deleted = false,
                BaseServerRevision = task.ServerRevision,
                ClientRevision = clientRevision,
                IdempotencyKey = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            };

            var response = await api
                .PostAsync<ChangePushResponseDto>("v1/sync/changes", body, accessToken, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsCancelled)
            {
                return new CommandResult(CommandOutcome.Cancelled, "操作已取消");
            }

            if (response.IsTransportFailure)
            {
                return new CommandResult(CommandOutcome.Offline, "离线，操作未提交");
            }

            if (response.IsUnauthorized || response.StatusCode == 403)
            {
                await _tokens.InvalidateAsync(cancellationToken).ConfigureAwait(false);
                return new CommandResult(CommandOutcome.SessionExpired, "会话已失效，请重新登录");
            }

            if (response.StatusCode == 429 || response.Error?.Code == WireErrorCodes.AuthRateLimited)
            {
                return new CommandResult(CommandOutcome.RateLimited, "操作过于频繁，请稍后再试");
            }

            if (response.IsConflict || response.Error?.Code == WireErrorCodes.SyncConflict)
            {
                var current = response.Error?.DetailsAs<SyncConflictDetailsDto>()?.Current;
                if (current is not null)
                {
                    await TryApplyCurrentAsync(projection, current, cancellationToken).ConfigureAwait(false);
                }

                return new CommandResult(CommandOutcome.Conflict, "任务已被其他设备修改，已刷新为服务器版本");
            }

            if (!response.IsSuccess || response.Value?.Change is not { } change)
            {
                return new CommandResult(CommandOutcome.ServerError, "服务端暂时不可用，操作未提交");
            }

            try
            {
                await projection.ApplyServerTaskAsync(change, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return new CommandResult(CommandOutcome.Protocol, "服务端返回的任务数据无法应用");
            }

            return response.Value.Replayed
                ? new CommandResult(CommandOutcome.Replayed, action == TaskAction.Complete ? "任务已完成" : "任务已归档")
                : new CommandResult(CommandOutcome.Applied, action == TaskAction.Complete ? "已标记完成" : "已归档");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CommandResult(CommandOutcome.Cancelled, "操作已取消");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task TryApplyCurrentAsync(
        TaskProjection projection,
        SyncConflictCurrentDto current,
        CancellationToken cancellationToken)
    {
        var change = new SyncChangeDto
        {
            Kind = current.Kind,
            Key = current.Key,
            ServerRevision = current.ServerRevision,
            Deleted = current.Deleted,
            Payload = current.Payload,
        };

        try
        {
            await projection.ApplyServerTaskAsync(change, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The server row could not be parsed; the conflict message still
            // stands and the next refresh pulls the authoritative state.
        }
    }

    private static JsonElement ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.Clone();
    }
}