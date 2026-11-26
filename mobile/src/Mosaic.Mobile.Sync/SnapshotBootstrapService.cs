using System.Security.Cryptography;
using System.Text.Json;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync;

public enum SnapshotBootstrapOutcome
{
    Restored = 0,
    NoSnapshot,
    SnapshotVanished,
    SnapshotTooLarge,
    Corrupt,
    Error,
}

public sealed record SnapshotBootstrapResult(
    SnapshotBootstrapOutcome Outcome,
    int TaskCount,
    SyncError? Error);

/// <summary>
/// Builds a projection from the server's latest backup snapshot.
///
/// Two layers must agree before anything is written: the manifest
/// (<c>/v1/backups/latest</c>) and the downloaded snapshot body. The body is
/// hashed and compared against <c>contentSha256</c>, the identity fields must
/// match field by field, and every item must parse exactly. Any mismatch aborts
/// the whole restore — the store is only touched after all checks pass, so a
/// failed bootstrap leaves the previous projection untouched.
/// </summary>
public sealed class SnapshotBootstrapService
{
    public const long MaxSnapshotBytes = 64L * 1024 * 1024;

    private readonly IMosaicApi _api;
    private readonly TimeProvider _time;

    public SnapshotBootstrapService(IMosaicApi api, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        _time = time ?? TimeProvider.System;
    }

    public async Task<SnapshotBootstrapResult> BootstrapAsync(
        ISyncStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var latest = await _api
            .GetAsync<BackupManifestDto>(
                "v1/backups/latest",
                authenticated: true,
                retryTransient: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (!latest.IsSuccess)
        {
            if (latest.StatusCode == 404 || latest.ErrorCode == WireErrorCodes.BackupNotFound)
            {
                return new SnapshotBootstrapResult(SnapshotBootstrapOutcome.NoSnapshot, 0, null);
            }

            return new SnapshotBootstrapResult(
                SnapshotBootstrapOutcome.Error,
                0,
                SyncErrorMapper.FromApi(latest.StatusCode, latest.Error));
        }

        var manifest = latest.Value;
        if (manifest is null
            || string.IsNullOrWhiteSpace(manifest.SnapshotId)
            || string.IsNullOrWhiteSpace(manifest.Cursor)
            || manifest.ServerRevision is not { } baselineRevision
            || baselineRevision < 0
            || manifest.ItemCount < 0
            || !IsSha256Hex(manifest.ContentSha256))
        {
            return Corrupt();
        }

        var snapshotId = manifest.SnapshotId!;
        var baselineCursor = manifest.Cursor!;
        var expectedSha = manifest.ContentSha256!;

        if (manifest.SizeBytes > MaxSnapshotBytes)
        {
            return new SnapshotBootstrapResult(
                SnapshotBootstrapOutcome.SnapshotTooLarge,
                0,
                new SyncError(SyncErrorKind.SnapshotTooLarge, WireErrorCodes.ClientResponseTooLarge));
        }

        var download = await _api
            .GetBytesAsync(
                $"v1/backups/{Uri.EscapeDataString(snapshotId)}",
                MaxSnapshotBytes,
                authenticated: true,
                retryTransient: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (!download.IsSuccess)
        {
            if (download.ErrorCode == WireErrorCodes.ClientResponseTooLarge)
            {
                return new SnapshotBootstrapResult(
                    SnapshotBootstrapOutcome.SnapshotTooLarge,
                    0,
                    new SyncError(SyncErrorKind.SnapshotTooLarge, WireErrorCodes.ClientResponseTooLarge));
            }

            if (download.StatusCode == 404 || download.ErrorCode == WireErrorCodes.BackupNotFound)
            {
                return new SnapshotBootstrapResult(
                    SnapshotBootstrapOutcome.SnapshotVanished,
                    0,
                    new SyncError(SyncErrorKind.SnapshotVanished, WireErrorCodes.BackupNotFound));
            }

            return new SnapshotBootstrapResult(
                SnapshotBootstrapOutcome.Error,
                0,
                SyncErrorMapper.FromApi(download.StatusCode, download.Error));
        }

        var bytes = download.Value;
        if (bytes is null)
        {
            return Corrupt();
        }

        var actualSha = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            return Corrupt();
        }

        BackupSnapshotDto? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<BackupSnapshotDto>(bytes, WireJson.Options);
        }
        catch (JsonException)
        {
            return Corrupt();
        }

        if (snapshot is null
            || snapshot.SnapshotId != snapshotId
            || snapshot.Cursor != baselineCursor
            || snapshot.ServerRevision != baselineRevision
            || snapshot.ItemCount != manifest.ItemCount
            || (snapshot.Items?.Count ?? 0) != manifest.ItemCount)
        {
            return Corrupt();
        }

        var restoredAt = _time.GetUtcNow();
        var fallbackTime = snapshot.CreatedAt ?? manifest.CreatedAt ?? restoredAt;

        var tasks = new List<SyncedTask>(manifest.ItemCount);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items ?? [])
        {
            var kind = item.Kind?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(item.Key))
            {
                return Corrupt();
            }

            if (!seen.Add(kind + "\0" + item.Key))
            {
                return Corrupt();
            }

            if (kind == "setting")
            {
                // Settings are not projected on mobile; the row is still valid.
                continue;
            }

            if (kind != "task")
            {
                return Corrupt();
            }

            var task = ReadSnapshotTask(item, fallbackTime);
            if (task is null)
            {
                return Corrupt();
            }

            tasks.Add(task);
        }

        try
        {
            await store
                .ReplaceProjectionAsync(
                    tasks,
                    new ProjectionBaseline(snapshotId, baselineRevision, fallbackTime, baselineCursor),
                    restoredAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A local write failure is not corruption: the previous projection
            // is still intact and the next run can retry the restore.
            return new SnapshotBootstrapResult(
                SnapshotBootstrapOutcome.Error,
                0,
                new SyncError(SyncErrorKind.Protocol, "sync.local_failure"));
        }

        return new SnapshotBootstrapResult(SnapshotBootstrapOutcome.Restored, tasks.Count, null);
    }

    /// <summary>
    /// Snapshot rows carry no server revision: the snapshot itself is the
    /// baseline. Stamping them with <c>0</c> keeps every later incremental
    /// change authoritative, which is exactly what the store's buried rule
    /// expects.
    /// </summary>
    private static SyncedTask? ReadSnapshotTask(BackupItemDto item, DateTimeOffset fallbackTime)
    {
        if (!Guid.TryParse(item.Key, out var id) || id == Guid.Empty)
        {
            return null;
        }

        if (item.Payload is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        TaskItemDto? payload;
        try
        {
            payload = element.Deserialize<TaskItemDto>(WireJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null
            || (payload.Id is { } payloadId && payloadId != id)
            || string.IsNullOrWhiteSpace(payload.Title)
            || payload.Revision < 0)
        {
            return null;
        }

        var createdAt = payload.CreatedAtUtc ?? payload.UpdatedAtUtc ?? fallbackTime;
        var updatedAt = payload.UpdatedAtUtc ?? payload.CreatedAtUtc ?? createdAt;

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
            ServerRevision = 0,
            DeviceId = null,
        };
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value is not { Length: 64 })
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static SnapshotBootstrapResult Corrupt() =>
        new(
            SnapshotBootstrapOutcome.Corrupt,
            0,
            new SyncError(SyncErrorKind.Corrupt, WireErrorCodes.BackupCorrupt));
}
