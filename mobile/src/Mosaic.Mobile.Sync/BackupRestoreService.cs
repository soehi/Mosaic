using System.Text.Json;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync;

public sealed record BackupRestoreResult(
    bool Restored,
    string? Cursor,
    int TaskCount,
    SyncError? Error,
    DateTimeOffset? SnapshotCreatedAt);

public sealed record BackupManifestResult(BackupManifestDto? Manifest, SyncError? Error)
{
    public bool Succeeded => Manifest is not null;
}

/// <summary>
/// The snapshot path: first-login restore (explicit, user confirmed) and the
/// recovery baseline after <c>sync.cursor_invalid</c>. The phone only maps
/// <c>items[{kind,key,payload,revision}]</c> into its own tables.
/// </summary>
public sealed class BackupRestoreService
{
    private readonly IMosaicApi _api;
    private readonly PortableSettings _settings;
    private readonly TimeProvider _time;

    public BackupRestoreService(IMosaicApi api, PortableSettings settings, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(settings);
        _api = api;
        _settings = settings;
        _time = time ?? TimeProvider.System;
    }

    public async Task<BackupManifestResult> GetLatestManifestAsync(CancellationToken cancellationToken = default)
    {
        var result = await _api.GetAsync<BackupManifestDto>(
                "v1/backups/latest",
                authenticated: true,
                retryTransient: true,
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess && result.Value is { } manifest
            ? new BackupManifestResult(manifest, null)
            : new BackupManifestResult(null, SyncErrorMapper.FromApi(result.StatusCode, result.Error));
    }

    /// <summary>
    /// Downloads the latest snapshot and replaces the local projection in one
    /// transaction. Callers must have asked the user before invoking this.
    /// </summary>
    public async Task<BackupRestoreResult> RestoreLatestAsync(
        ISyncStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var latest = await _api.GetAsync<BackupManifestDto>(
                "v1/backups/latest",
                authenticated: true,
                retryTransient: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!latest.IsSuccess || latest.Value is null || string.IsNullOrWhiteSpace(latest.Value.SnapshotId))
        {
            return new BackupRestoreResult(
                false,
                null,
                0,
                SyncErrorMapper.FromApi(latest.StatusCode, latest.Error),
                null);
        }

        var snapshot = await _api.GetAsync<BackupSnapshotDto>(
                $"v1/backups/{Uri.EscapeDataString(latest.Value.SnapshotId)}",
                authenticated: true,
                retryTransient: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!snapshot.IsSuccess || snapshot.Value is null)
        {
            return new BackupRestoreResult(
                false,
                null,
                0,
                SyncErrorMapper.FromApi(snapshot.StatusCode, snapshot.Error),
                null);
        }

        var mapped = MapSnapshot(snapshot.Value, _time.GetUtcNow());
        await store.ReplaceProjectionAsync(mapped.Tasks, mapped.Settings, snapshot.Value.Cursor, cancellationToken)
            .ConfigureAwait(false);
        _settings.ApplyAll(mapped.Settings);

        return new BackupRestoreResult(
            true,
            snapshot.Value.Cursor,
            mapped.Tasks.Count,
            null,
            snapshot.Value.CreatedAt);
    }

    private static (List<SyncedTask> Tasks, List<StoredSetting> Settings) MapSnapshot(
        BackupSnapshotDto snapshot,
        DateTimeOffset now)
    {
        var tasks = new List<SyncedTask>();
        var settings = new List<StoredSetting>();

        foreach (var item in snapshot.Items ?? [])
        {
            var kind = item.Kind?.Trim().ToLowerInvariant();
            if (kind == "task")
            {
                if (TryMapTask(item, now) is { } task)
                {
                    tasks.Add(task);
                }
            }
            else if (kind == "setting" && PortableSettingKeys.IsKnown(item.Key))
            {
                if (item.Payload is { ValueKind: JsonValueKind.Object } payload
                    && payload.TryGetProperty("value", out var value))
                {
                    settings.Add(new StoredSetting(
                        item.Key!,
                        value.GetRawText(),
                        // Snapshot entries are a baseline; the next incremental
                        // page must win, so its serverRevision starts at 0.
                        0,
                        snapshot.CreatedAt));
                }
            }
        }

        return (tasks, settings);
    }

    private static SyncedTask? TryMapTask(BackupItemDto item, DateTimeOffset now)
    {
        if (item.Payload is not { ValueKind: JsonValueKind.Object } payload)
        {
            return null;
        }

        TaskItemDto? dto;
        try
        {
            dto = payload.Deserialize<TaskItemDto>(WireJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        var id = dto?.Id
            ?? (Guid.TryParse(item.Key, out var keyId) ? keyId : null);
        if (id is null || dto is null)
        {
            return null;
        }

        var createdAt = dto.CreatedAtUtc ?? dto.UpdatedAtUtc ?? now;
        return new SyncedTask
        {
            Id = id.Value,
            Title = dto.Title ?? string.Empty,
            Notes = dto.Notes ?? string.Empty,
            IsCompleted = dto.IsCompleted,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = dto.UpdatedAtUtc ?? createdAt,
            Revision = dto.Revision != 0 ? dto.Revision : item.Revision ?? 0,
            CompletedAtUtc = dto.CompletedAtUtc,
            ArchivedAtUtc = dto.ArchivedAtUtc,
            RestoredAtUtc = dto.RestoredAtUtc,
            ServerRevision = 0,
            DeviceId = null,
        };
    }
}
