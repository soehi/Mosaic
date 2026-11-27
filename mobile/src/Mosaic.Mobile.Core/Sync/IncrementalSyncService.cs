using Mosaic.Mobile.Core.Api;
using Mosaic.Mobile.Core.Projection;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Core.Sync;

public enum PullOutcome
{
    Completed = 0,
    SessionExpired,
    Offline,
    NeedsSnapshot,
    Error,
    Cancelled,

    /// <summary>The server sent a task row this client cannot apply.</summary>
    Protocol,
}

public sealed record PullResult(PullOutcome Outcome, int Applied, string? ErrorCode)
{
    public bool IsCompleted => Outcome == PullOutcome.Completed;
}

/// <summary>
/// Pulls <c>/v1/sync/changes</c> pages from the projection cursor to the head.
/// Only task entries are applied. A page is committed together with the cursor
/// that follows it, so a crash between pages resumes exactly where it stopped.
/// </summary>
public sealed class IncrementalSyncService
{
    public const int PageSize = 200;

    private const int MaxPagesPerRun = 200;

    public async Task<PullResult> RunAsync(
        IMosaicApi api,
        TaskProjection projection,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(projection);

        var cursor = projection.Cursor;
        var applied = 0;
        for (var page = 0; page < MaxPagesPerRun; page++)
        {
            var path = "v1/sync/changes?limit=" + PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                path += "&cursor=" + Uri.EscapeDataString(cursor);
            }

            var response = await api.GetAsync<SyncChangePageDto>(path, accessToken, cancellationToken).ConfigureAwait(false);
            if (response.IsCancelled)
            {
                return new PullResult(PullOutcome.Cancelled, applied, null);
            }

            if (response.IsTransportFailure)
            {
                return new PullResult(PullOutcome.Offline, applied, null);
            }

            if (response.IsUnauthorized || response.StatusCode == 403)
            {
                return new PullResult(PullOutcome.SessionExpired, applied, response.Error?.Code);
            }

            if (response.StatusCode is 400 or 410 && IsSnapshotRequired(response))
            {
                return new PullResult(PullOutcome.NeedsSnapshot, applied, WireErrorCodes.SyncSnapshotRequired);
            }

            if (!response.IsSuccess || response.Value?.Changes is null)
            {
                return new PullResult(PullOutcome.Error, applied, response.Error?.Code);
            }

            var current = response.Value;
            try
            {
                await projection
                    .ApplyChangesAsync(current.Changes, current.NextCursor ?? cursor, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The page transaction was rolled back, so the cursor still points
                // at the last committed page; nothing is half-applied.
                return new PullResult(PullOutcome.Protocol, applied, "sync.protocol");
            }
            applied += current.Changes.Count;
            cursor = current.NextCursor ?? cursor;
            if (!current.HasMore)
            {
                return new PullResult(PullOutcome.Completed, applied, null);
            }
        }

        return new PullResult(PullOutcome.Error, applied, "sync.too_many_pages");
    }

    private static bool IsSnapshotRequired(ApiResponse<SyncChangePageDto> response)
    {
        if (response.Error?.Details is not { } details)
        {
            return false;
        }

        return details.TryGetProperty("requiresSnapshot", out var flag) && flag.ValueKind == System.Text.Json.JsonValueKind.True;
    }
}