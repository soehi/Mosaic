using System.Text.Json;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync;

public sealed record ProjectionResult(SyncPage Page, int ChangeCount);

/// <summary>
/// Translates one wire page into a storage page. Strict by design: anything the
/// client cannot interpret exactly rejects the whole page, so a partial commit
/// can never move the cursor past a change it did not apply. Settings are not
/// projected — the mobile app owns no portable settings any more — but they are
/// still valid rows, so they are skipped rather than treated as protocol errors.
/// </summary>
public static class ChangeProjector
{
    public static ProjectionResult Project(
        SyncChangesResponseDto? response,
        DateTimeOffset syncedAt,
        string? expectedCursor)
    {
        if (response is null)
        {
            throw new SyncProtocolException(
                LocalTransportCodes.Protocol,
                "同步响应为空。");
        }

        var nextCursor = response.NextCursor;
        if (string.IsNullOrWhiteSpace(nextCursor))
        {
            throw new SyncProtocolException(
                WireErrorCodes.SyncInvalid,
                "同步响应缺少下一页游标。");
        }

        var tasks = new Dictionary<Guid, SyncedTask>();
        var tombstones = new Dictionary<Guid, TaskTombstone>();
        var count = 0;

        foreach (var change in response.Changes ?? [])
        {
            switch (change.ResolvedKind)
            {
                case ChangeKind.Unknown:
                    throw new SyncProtocolException(
                        WireErrorCodes.SyncInvalid,
                        "同步响应包含未知的变更类别。");
                case ChangeKind.Setting:
                    // The server still syncs desktop settings; this client keeps
                    // no copy of them and must not fail on keys it never reads.
                    continue;
            }

            if (change.ResolvedOperation == ChangeOperation.Unknown)
            {
                throw new SyncProtocolException(
                    WireErrorCodes.SyncInvalid,
                    "同步响应包含未知的变更操作。");
            }

            if (change.ServerRevision <= 0)
            {
                throw new SyncProtocolException(
                    WireErrorCodes.SyncInvalid,
                    "增量变更缺少正的服务器版本号。");
            }

            if (!Guid.TryParse(change.Key, out var id) || id == Guid.Empty)
            {
                throw new SyncProtocolException(
                    WireErrorCodes.SyncInvalid,
                    "同步响应的任务标识无效。");
            }

            if (change.ResolvedOperation == ChangeOperation.Delete)
            {
                ApplyTombstone(
                    id,
                    new TaskTombstone(
                        id,
                        change.ServerRevision,
                        change.CreatedAt ?? change.UpdatedAtUtc),
                    tasks,
                    tombstones);
                count++;
                continue;
            }

            var task = ReadTask(change, id, syncedAt);
            ApplyTask(id, task, tasks, tombstones);
            count++;
        }

        return new ProjectionResult(
            new SyncPage(
                tasks.Values.ToList(),
                tombstones.Values.ToList(),
                nextCursor,
                response.HasMore,
                syncedAt,
                expectedCursor),
            count);
    }

    private static SyncedTask ReadTask(ChangeDto change, Guid id, DateTimeOffset syncedAt)
    {
        if (change.Payload is not { ValueKind: JsonValueKind.Object } element)
        {
            throw new SyncProtocolException(
                WireErrorCodes.SyncInvalid,
                "同步响应的任务负载缺失。");
        }

        TaskItemDto? payload;
        try
        {
            payload = element.Deserialize<TaskItemDto>(WireJson.Options);
        }
        catch (JsonException)
        {
            throw new SyncProtocolException(
                WireErrorCodes.SyncInvalid,
                "同步响应的任务负载无法解析。");
        }

        if (payload is null)
        {
            throw new SyncProtocolException(
                WireErrorCodes.SyncInvalid,
                "同步响应的任务负载为空。");
        }

        if (payload.Id is { } payloadId && payloadId != id)
        {
            throw new SyncProtocolException(
                WireErrorCodes.SyncInvalid,
                "同步响应的任务标识与负载不一致。");
        }

        if (string.IsNullOrWhiteSpace(payload.Title))
        {
            throw new SyncProtocolException(
                WireErrorCodes.SyncInvalid,
                "同步响应的任务缺少标题。");
        }

        if (payload.Revision < 0)
        {
            throw new SyncProtocolException(
                WireErrorCodes.SyncInvalid,
                "同步响应的任务版本号无效。");
        }

        var createdAt = payload.CreatedAtUtc
            ?? payload.UpdatedAtUtc
            ?? change.CreatedAt
            ?? change.UpdatedAtUtc
            ?? syncedAt;
        var updatedAt = payload.UpdatedAtUtc
            ?? payload.CreatedAtUtc
            ?? change.CreatedAt
            ?? change.UpdatedAtUtc
            ?? createdAt;

        return new SyncedTask
        {
            Id = id,
            Title = payload.Title,
            Notes = payload.Notes ?? string.Empty,
            IsCompleted = payload.IsCompleted,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = updatedAt,
            Revision = payload.Revision,
            CompletedAtUtc = payload.CompletedAtUtc,
            ArchivedAtUtc = payload.ArchivedAtUtc,
            RestoredAtUtc = payload.RestoredAtUtc,
            ServerRevision = change.ServerRevision,
            DeviceId = change.DeviceId,
        };
    }

    /// <summary>
    /// Same-page aggregation follows the server's authority order: higher
    /// <c>serverRevision</c> wins, an equal revision is a delete, and two
    /// different payloads at one revision mean the page cannot be trusted.
    /// </summary>
    private static void ApplyTask(
        Guid id,
        SyncedTask candidate,
        Dictionary<Guid, SyncedTask> tasks,
        Dictionary<Guid, TaskTombstone> tombstones)
    {
        if (tombstones.TryGetValue(id, out var tombstone))
        {
            if (tombstone.ServerRevision >= candidate.ServerRevision)
            {
                return;
            }

            tombstones.Remove(id);
        }

        if (tasks.TryGetValue(id, out var existing))
        {
            if (existing.ServerRevision > candidate.ServerRevision)
            {
                return;
            }

            if (existing.ServerRevision == candidate.ServerRevision)
            {
                if (!existing.Equals(candidate))
                {
                    throw new SyncProtocolException(
                        WireErrorCodes.SyncConflict,
                        "同步响应在同一版本上给出了不同的任务内容。");
                }

                return;
            }
        }

        tasks[id] = candidate;
    }

    private static void ApplyTombstone(
        Guid id,
        TaskTombstone candidate,
        Dictionary<Guid, SyncedTask> tasks,
        Dictionary<Guid, TaskTombstone> tombstones)
    {
        if (tombstones.TryGetValue(id, out var existing)
            && existing.ServerRevision >= candidate.ServerRevision)
        {
            return;
        }

        if (tasks.TryGetValue(id, out var task))
        {
            if (task.ServerRevision > candidate.ServerRevision)
            {
                return;
            }

            tasks.Remove(id);
        }

        tombstones[id] = candidate;
    }
}
