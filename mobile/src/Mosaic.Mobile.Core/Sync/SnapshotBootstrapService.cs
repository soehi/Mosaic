using Mosaic.Mobile.Core.Api;
using Mosaic.Mobile.Core.Projection;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Core.Sync;

public enum BootstrapOutcome
{
    Restored = 0,
    NoSnapshot,
    Corrupt,
    TooLarge,
    SessionExpired,
    Offline,
    Cancelled,
    Error,
}

public sealed record BootstrapResult(BootstrapOutcome Outcome, int TaskCount, string? ErrorCode)
{
    public bool IsRestored => Outcome == BootstrapOutcome.Restored;
}

/// <summary>
/// Builds the projection baseline from the server's latest uploaded backup.
/// The manifest and the snapshot body must agree on id, item count, cursor and
/// the canonical content digest before anything replaces the local projection.
/// Per-task <c>serverRevision</c> is left at 0: the snapshot-level revision is
/// source metadata and must not be stamped onto individual tasks; the following
/// incremental pull supplies the per-item value.
/// </summary>
public sealed class SnapshotBootstrapService
{
    public const long MaxSnapshotBytes = 64L * 1024 * 1024;

    private readonly TimeProvider _time;

    public SnapshotBootstrapService(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public async Task<BootstrapResult> RunAsync(
        IMosaicApi api,
        TaskProjection projection,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(projection);

        var latest = await api.GetAsync<BackupManifestDto>("v1/backups/latest", accessToken, cancellationToken).ConfigureAwait(false);
        switch (Classify(latest))
        {
            case BootstrapOutcome.NoSnapshot:
                return new BootstrapResult(BootstrapOutcome.NoSnapshot, 0, latest.Error?.Code);
            case BootstrapOutcome.SessionExpired:
                return new BootstrapResult(BootstrapOutcome.SessionExpired, 0, latest.Error?.Code);
            case BootstrapOutcome.Offline:
                return new BootstrapResult(BootstrapOutcome.Offline, 0, null);
            case BootstrapOutcome.Cancelled:
                return new BootstrapResult(BootstrapOutcome.Cancelled, 0, null);
            case BootstrapOutcome.Error:
                return new BootstrapResult(BootstrapOutcome.Error, 0, latest.Error?.Code);
        }

        var manifest = latest.Value!;
        if (string.IsNullOrWhiteSpace(manifest.SnapshotId)
            || string.IsNullOrWhiteSpace(manifest.ContentSha256)
            || manifest.ContentSha256.Length != 64
            || manifest.ItemCount < 0
            || string.IsNullOrWhiteSpace(manifest.Cursor))
        {
            return new BootstrapResult(BootstrapOutcome.Corrupt, 0, WireErrorCodes.BackupInvalid);
        }

        var download = await api
            .GetBytesAsync(
                $"v1/backups/{Uri.EscapeDataString(manifest.SnapshotId)}",
                MaxSnapshotBytes,
                accessToken,
                cancellationToken)
            .ConfigureAwait(false);

        if (download.IsCancelled)
        {
            return new BootstrapResult(BootstrapOutcome.Cancelled, 0, null);
        }

        if (download.IsTransportFailure)
        {
            return new BootstrapResult(BootstrapOutcome.Offline, 0, null);
        }

        if (download.StatusCode == 413 || download.Error?.Code == WireErrorCodes.ClientResponseTooLarge)
        {
            return new BootstrapResult(BootstrapOutcome.TooLarge, 0, WireErrorCodes.ClientResponseTooLarge);
        }

        if (!download.IsSuccess || download.Value is null)
        {
            var outcome = download.StatusCode is 404 || download.Error?.Code == WireErrorCodes.BackupNotFound
                ? BootstrapOutcome.NoSnapshot
                : Classify(download);
            return new BootstrapResult(outcome, 0, download.Error?.Code);
        }

        BackupSnapshotDto? snapshot;
        try
        {
            snapshot = WireJson.Deserialize<BackupSnapshotDto>(download.Value);
        }
        catch (System.Text.Json.JsonException)
        {
            return new BootstrapResult(BootstrapOutcome.Corrupt, 0, WireErrorCodes.BackupInvalid);
        }

        if (snapshot is null
            || snapshot.Items is null
            || !string.Equals(snapshot.SnapshotId, manifest.SnapshotId, StringComparison.Ordinal)
            || snapshot.Items.Count != manifest.ItemCount
            || !string.Equals(NormalizeCursor(snapshot.Cursor), NormalizeCursor(manifest.Cursor), StringComparison.Ordinal))
        {
            return new BootstrapResult(BootstrapOutcome.Corrupt, 0, WireErrorCodes.BackupInvalid);
        }

        var computedHash = BackupCanonicalizer.ComputeSha256Hex(snapshot.Items);
        if (!string.Equals(computedHash, manifest.ContentSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            return new BootstrapResult(BootstrapOutcome.Corrupt, 0, WireErrorCodes.BackupHashMismatch);
        }

        var tasks = new List<SyncedTask>(snapshot.Items.Count);
        foreach (var item in snapshot.Items)
        {
            var kind = item.Kind?.Trim().ToLowerInvariant();
            if (string.Equals(kind, SyncKinds.Setting, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(kind, SyncKinds.Task, StringComparison.Ordinal))
            {
                return new BootstrapResult(BootstrapOutcome.Corrupt, 0, WireErrorCodes.BackupInvalid);
            }

            if (!Guid.TryParse(item.Key, out var id)
                || !TaskPayload.TryRead(item.Payload?.GetRawText(), out var fields, out _)
                || fields.Id != id)
            {
                return new BootstrapResult(BootstrapOutcome.Corrupt, 0, WireErrorCodes.BackupInvalid);
            }

            tasks.Add(new SyncedTask
            {
                Id = id,
                Title = fields.Title,
                PayloadJson = item.Payload!.Value.GetRawText(),
                Revision = fields.Revision,
                ServerRevision = 0,
                IsCompleted = fields.IsCompleted,
                ArchivedAtUtc = fields.ArchivedAtUtc,
                CreatedAtUtc = fields.CreatedAtUtc,
            });
        }

        var baseline = new ProjectionBaseline(
            manifest.SnapshotId,
            manifest.ServerRevision,
            snapshot.CreatedAt ?? manifest.CreatedAt ?? _time.GetUtcNow(),
            manifest.Cursor,
            _time.GetUtcNow());

        await projection.ReplaceAsync(tasks, baseline, cancellationToken).ConfigureAwait(false);
        return new BootstrapResult(BootstrapOutcome.Restored, tasks.Count, null);
    }

    private static BootstrapOutcome Classify(ApiResponse<BackupManifestDto> response)
    {
        if (response.IsCancelled)
        {
            return BootstrapOutcome.Cancelled;
        }

        if (response.IsTransportFailure)
        {
            return BootstrapOutcome.Offline;
        }

        if (response.IsUnauthorized || response.StatusCode == 403)
        {
            return BootstrapOutcome.SessionExpired;
        }

        if (response.StatusCode is 404 || response.Error?.Code == WireErrorCodes.BackupNotFound)
        {
            return BootstrapOutcome.NoSnapshot;
        }

        return response.IsSuccess && response.Value is not null
            ? BootstrapOutcome.Restored
            : BootstrapOutcome.Error;
    }

    private static BootstrapOutcome Classify(ApiResponse<byte[]> response) =>
        response.IsUnauthorized || response.StatusCode == 403
            ? BootstrapOutcome.SessionExpired
            : response.IsTransportFailure
                ? BootstrapOutcome.Offline
                : BootstrapOutcome.Error;

    private static string? NormalizeCursor(string? cursor) =>
        string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();
}