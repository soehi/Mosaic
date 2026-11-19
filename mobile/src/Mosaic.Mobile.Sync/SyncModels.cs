using System.Net;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync;

public enum SyncStatusType
{
    Idle = 0,
    Syncing,
    Succeeded,
    Offline,
    NeedsSignIn,
    RebuildRequired,
    Failed,
}

public sealed record SyncStatusSnapshot(
    SyncStatusType Status,
    DateTimeOffset? LastSyncedAt,
    int ChangeCount,
    string? Code);

public enum SyncErrorKind
{
    None = 0,
    Offline,
    SessionExpired,
    RateLimited,
    ServerError,
    Protocol,
    Cancelled,
    NotFound,
}

public sealed record SyncError(SyncErrorKind Kind, string? Code)
{
    public static SyncError Cancelled { get; } = new(SyncErrorKind.Cancelled, "client.cancelled");
}

public sealed record SyncReport(
    bool Succeeded,
    int Pages,
    int Changes,
    bool RebuiltFromSnapshot,
    bool NeedsSnapshot,
    SyncError? Error,
    DateTimeOffset? LastSyncedAt)
{
    public static SyncReport Failure(SyncError error) =>
        new(false, 0, 0, false, false, error, null);
}

public static class SyncErrorMapper
{
    public static SyncError FromApi(int statusCode, WireErrorDto? error)
    {
        var code = error?.Code;
        if (code is LocalTransportCodes.Network or LocalTransportCodes.Timeout || statusCode == 0)
        {
            return new SyncError(SyncErrorKind.Offline, code ?? LocalTransportCodes.Network);
        }

        if (statusCode == 401 || WireErrorCodes.IsSessionGone(code))
        {
            return new SyncError(SyncErrorKind.SessionExpired, code ?? "auth.session_expired");
        }

        if (statusCode == (int)HttpStatusCode.TooManyRequests || code == WireErrorCodes.AuthRateLimited)
        {
            return new SyncError(SyncErrorKind.RateLimited, code ?? "auth.rate_limited");
        }

        if (statusCode == (int)HttpStatusCode.NotFound)
        {
            return new SyncError(SyncErrorKind.NotFound, code ?? "server.not_found");
        }

        if (statusCode >= 500)
        {
            return new SyncError(SyncErrorKind.ServerError, code ?? WireErrorCodes.ServerUnavailable);
        }

        return new SyncError(SyncErrorKind.Protocol, code ?? $"http.{statusCode}");
    }
}
