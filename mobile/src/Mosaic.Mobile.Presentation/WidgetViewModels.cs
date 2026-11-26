using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;

namespace Mosaic.Mobile.Presentation;

/// <summary>How a widget command ended, in a platform-neutral form.</summary>
public enum WidgetWriteStatus
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

public sealed record WidgetWriteResult(WidgetWriteStatus Status, string? Code = null);

/// <summary>Why a widget could not show current-account tasks.</summary>
public enum WidgetBlockedReason
{
    None,
    NeedsSignIn,
    NeedsSync,
    Unavailable,
}

public sealed record WidgetTaskLine(Guid Id, string Title);

public sealed record WidgetSnapshot(
    WidgetBlockedReason BlockedReason,
    int OpenCount,
    IReadOnlyList<WidgetTaskLine> Tasks)
{
    public static WidgetSnapshot Blocked(WidgetBlockedReason reason) =>
        new(reason, 0, Array.Empty<WidgetTaskLine>());
}

/// <summary>
/// Platform-neutral widget projection reader. It resolves the trusted identity
/// from the shared auth session, opens the matching projection through
/// <see cref="ProjectionStoreFactory"/> and reads one consistent snapshot.
/// </summary>
public sealed class WidgetTaskReader
{
    private readonly AuthSessionManager _auth;
    private readonly ProjectionSession _projection;

    public WidgetTaskReader(AuthSessionManager auth, ProjectionSession projection)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(projection);
        _auth = auth;
        _projection = projection;
    }

    public async Task<WidgetSnapshot> ReadAsync(
        string serverUrl,
        int maxRows,
        long generation,
        CancellationToken cancellationToken = default)
    {
        if (maxRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRows));
        }

        if (!IsCurrent(generation))
        {
            return WidgetSnapshot.Blocked(WidgetBlockedReason.NeedsSignIn);
        }

        var trusted = await TrustedUserIdAsync().ConfigureAwait(false);
        if (trusted is null)
        {
            return WidgetSnapshot.Blocked(WidgetBlockedReason.NeedsSignIn);
        }

        if (!TryCreateIdentity(serverUrl, trusted, out var identity))
        {
            return WidgetSnapshot.Blocked(WidgetBlockedReason.Unavailable);
        }

        if (!IsCurrent(generation))
        {
            return WidgetSnapshot.Blocked(WidgetBlockedReason.NeedsSignIn);
        }

        try
        {
            var open = await _projection.OpenAsync(identity, cancellationToken).ConfigureAwait(false);
            if (!open.Opened || _projection.Store is null)
            {
                return WidgetSnapshot.Blocked(WidgetBlockedReason.NeedsSync);
            }
        }
        catch (Exception)
        {
            return WidgetSnapshot.Blocked(WidgetBlockedReason.Unavailable);
        }

        if (!IsCurrent(generation) || !_projection.Identity?.OwnerKey.Equals(identity.OwnerKey, StringComparison.Ordinal) == true)
        {
            return WidgetSnapshot.Blocked(WidgetBlockedReason.NeedsSignIn);
        }

        try
        {
            var store = _projection.Store!;
            var tasks = await store
                .ListTasksAsync(MobileTaskFilter.Open, cancellationToken)
                .ConfigureAwait(false);
            var lines = tasks.Count <= maxRows
                ? tasks.Select(t => new WidgetTaskLine(t.Id, t.Title)).ToArray()
                : tasks.Take(maxRows).Select(t => new WidgetTaskLine(t.Id, t.Title)).ToArray();
            return new WidgetSnapshot(WidgetBlockedReason.None, tasks.Count, lines);
        }
        catch (Exception)
        {
            return WidgetSnapshot.Blocked(WidgetBlockedReason.Unavailable);
        }
    }

    public async Task<SyncedTask?> FindTaskAsync(
        Guid taskId,
        string serverUrl,
        long generation,
        CancellationToken cancellationToken = default)
    {
        var store = _projection.Store;
        if (store is null || !IsCurrent(generation))
        {
            return null;
        }

        var trusted = await TrustedUserIdAsync().ConfigureAwait(false);
        if (trusted is null
            || !TryCreateIdentity(serverUrl, trusted, out var expected)
            || !IsCurrent(generation)
            || !store.Identity.OwnerKey.Equals(expected.OwnerKey, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return await store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool IsCurrent(long generation) =>
        _auth.Generation == generation && _auth.State == AuthState.SignedIn && !_auth.CacheReadOnly;

    private async Task<string?> TrustedUserIdAsync()
    {
        var verified = _auth.VerifiedUserId;
        if (!string.IsNullOrWhiteSpace(verified))
        {
            return verified;
        }

        var result = await _auth.VerifyIdentityAsync().ConfigureAwait(false);
        return result.Status switch
        {
            IdentityVerificationStatus.Verified => result.UserId,
            IdentityVerificationStatus.Rejected => null,
            _ => _auth.UserId,
        };
    }

    private static bool TryCreateIdentity(string serverUrl, string userId, out ProjectionIdentity identity)
    {
        identity = null!;
        try
        {
            identity = ProjectionIdentity.Create(serverUrl, userId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>Maps narrow-write results to shared widget strings.</summary>
public static class WidgetWriteText
{
    public static string Describe(LocalizationService localizer, WidgetWriteResult result) =>
        result.Status switch
        {
            WidgetWriteStatus.Applied => localizer.T(StringKeys.WidgetAppliedWrite),
            WidgetWriteStatus.Replayed => localizer.T(StringKeys.WidgetReplayedWrite),
            WidgetWriteStatus.Conflict => localizer.T(StringKeys.WidgetConflictWrite),
            WidgetWriteStatus.SessionExpired => localizer.T(StringKeys.WidgetSessionExpiredWrite),
            WidgetWriteStatus.Offline => result.Code == LocalTransportCodes.Timeout
                ? localizer.T(StringKeys.WidgetTimeoutWrite)
                : localizer.T(StringKeys.WidgetOfflineWrite),
            WidgetWriteStatus.ServerError => localizer.T(StringKeys.WidgetServerErrorWrite),
            WidgetWriteStatus.RateLimited => localizer.T(StringKeys.WidgetRateLimitedWrite),
            WidgetWriteStatus.Protocol => localizer.T(StringKeys.WidgetProtocolWrite),
            _ => string.Empty,
        };

    public static WidgetWriteResult From(TaskWriteResult result)
    {
        var status = result.Outcome switch
        {
            TaskWriteOutcome.Applied => WidgetWriteStatus.Applied,
            TaskWriteOutcome.Replayed => WidgetWriteStatus.Replayed,
            TaskWriteOutcome.Conflict => WidgetWriteStatus.Conflict,
            TaskWriteOutcome.SessionExpired => WidgetWriteStatus.SessionExpired,
            TaskWriteOutcome.Offline => WidgetWriteStatus.Offline,
            TaskWriteOutcome.ServerError => WidgetWriteStatus.ServerError,
            TaskWriteOutcome.RateLimited => WidgetWriteStatus.RateLimited,
            TaskWriteOutcome.Protocol => WidgetWriteStatus.Protocol,
            _ => WidgetWriteStatus.Cancelled,
        };
        return new WidgetWriteResult(status, result.Code);
    }
}
