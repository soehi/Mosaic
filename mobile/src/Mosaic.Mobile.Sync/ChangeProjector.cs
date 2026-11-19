using System.Text.Json;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync;

public sealed record ProjectionResult(
    SyncPage Page,
    int ChangeCount,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Translates wire changes into storage projections. Unknown kinds, unknown
/// settings and malformed payloads are skipped with a diagnostic: an old app
/// must never delete data because a newer server added something it does not
/// understand.
/// </summary>
public static class ChangeProjector
{
    private const int MaxDiagnostics = 20;

    public static ProjectionResult Project(SyncChangesResponseDto? response, DateTimeOffset syncedAt)
    {
        var tasks = new List<SyncedTask>();
        var tombstones = new List<TaskTombstone>();
        var settings = new List<StoredSetting>();
        var diagnostics = new List<string>();
        var count = 0;

        foreach (var change in response?.Changes ?? [])
        {
            var operation = change.ResolvedOperation;
            if (operation == ChangeOperation.Unknown)
            {
                Add(diagnostics, $"operation_unknown:{change.Operation}");
                continue;
            }

            switch (change.ResolvedKind)
            {
                case ChangeKind.Task:
                    if (ProjectTask(change, operation, syncedAt, tasks, tombstones, diagnostics))
                    {
                        count++;
                    }

                    break;
                case ChangeKind.Setting:
                    if (ProjectSetting(change, operation, settings, diagnostics))
                    {
                        count++;
                    }

                    break;
                default:
                    Add(diagnostics, $"kind_unknown:{change.Kind}");
                    break;
            }
        }

        var page = new SyncPage(
            tasks,
            tombstones,
            settings,
            response?.NextCursor,
            syncedAt);
        return new ProjectionResult(page, count, diagnostics);
    }

    private static bool ProjectTask(
        ChangeDto change,
        ChangeOperation operation,
        DateTimeOffset syncedAt,
        List<SyncedTask> tasks,
        List<TaskTombstone> tombstones,
        List<string> diagnostics)
    {
        if (!Guid.TryParse(change.Key, out var keyId))
        {
            Add(diagnostics, "task_key_invalid");
            return false;
        }

        if (operation == ChangeOperation.Delete)
        {
            tombstones.Add(new TaskTombstone(keyId, change.ServerRevision, change.EffectiveTimestamp));
            return true;
        }

        if (change.Payload is not { ValueKind: JsonValueKind.Object } payloadElement)
        {
            Add(diagnostics, "task_payload_missing");
            return false;
        }

        TaskItemDto? payload;
        try
        {
            payload = payloadElement.Deserialize<TaskItemDto>(WireJson.Options);
        }
        catch (JsonException)
        {
            Add(diagnostics, "task_payload_invalid");
            return false;
        }

        if (payload is null)
        {
            Add(diagnostics, "task_payload_invalid");
            return false;
        }

        if (payload.Id is { } payloadId && payloadId != keyId)
        {
            Add(diagnostics, "task_key_mismatch");
        }

        var createdAt = payload.CreatedAtUtc
            ?? payload.UpdatedAtUtc
            ?? change.EffectiveTimestamp
            ?? syncedAt;
        var updatedAt = payload.UpdatedAtUtc ?? change.EffectiveTimestamp ?? createdAt;

        tasks.Add(new SyncedTask
        {
            Id = keyId,
            Title = payload.Title ?? string.Empty,
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
        });
        return true;
    }

    private static bool ProjectSetting(
        ChangeDto change,
        ChangeOperation operation,
        List<StoredSetting> settings,
        List<string> diagnostics)
    {
        var key = change.Key;
        if (string.IsNullOrWhiteSpace(key))
        {
            Add(diagnostics, "setting_key_invalid");
            return false;
        }

        if (!PortableSettingKeys.IsKnown(key))
        {
            // Unknown keys are expected as the desktop grows; ignore quietly
            // for behaviour but keep a bounded diagnostic for support.
            Add(diagnostics, $"setting_unknown:{key}");
            return false;
        }

        string valueJson;
        if (operation == ChangeOperation.Delete)
        {
            valueJson = "null";
        }
        else
        {
            if (change.Payload is not { ValueKind: JsonValueKind.Object } payload
                || !payload.TryGetProperty("value", out var value))
            {
                Add(diagnostics, "setting_payload_invalid");
                return false;
            }

            valueJson = value.GetRawText();
        }

        settings.Add(new StoredSetting(
            key,
            valueJson,
            change.ServerRevision,
            change.EffectiveTimestamp));
        return true;
    }

    private static void Add(List<string> diagnostics, string value)
    {
        if (diagnostics.Count >= MaxDiagnostics)
        {
            diagnostics.RemoveAt(0);
        }

        diagnostics.Add(value);
    }
}
