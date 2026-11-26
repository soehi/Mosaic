using System.Net;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync;

/// <summary>
/// What the UI needs to know about the projection. <see cref="Ready"/> means a
/// complete run finished (<c>HasMore=false</c>, every page applied) against a
/// known cursor. <see cref="Incomplete"/> means the page budget ran out first:
/// the cursor advanced but the projection has not caught up yet.
/// </summary>
public enum SyncStatusType
{
    Idle = 0,
    Syncing,
    Ready,
    Bootstrapping,
    Empty,
    Incomplete,
    Offline,
    NeedsSignIn,
    NeedsRestore,
    SnapshotVanished,
    SnapshotTooLarge,
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
    SnapshotTooLarge,
    SnapshotVanished,
    Corrupt,
}

public sealed record SyncError(SyncErrorKind Kind, string? Code)
{
    public static SyncError Cancelled { get; } = new(SyncErrorKind.Cancelled, "client.cancelled");
}

/// <summary>
/// A wire response the projection cannot accept without losing data integrity.
/// The page is rejected whole; nothing is committed. <see cref="Code"/> is a
/// machine code, never server prose.
/// </summary>
public sealed class SyncProtocolException : InvalidOperationException
{
    public SyncProtocolException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record SyncReport(
    bool Succeeded,
    int Pages,
    int Changes,
    bool Bootstrapped,
    bool Incomplete,
    SyncError? Error,
    DateTimeOffset? LastCompleteSyncAt)
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
