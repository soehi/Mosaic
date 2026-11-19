using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync;

public enum SyncWriteStatus
{
    /// <summary>Server accepted and stored the change.</summary>
    Applied = 0,

    /// <summary>The idempotency key was already applied; the response is the stored change.</summary>
    Replayed,

    /// <summary>409 <c>sync.conflict</c>; <see cref="SyncWriteResult.Current"/> holds the winning record.</summary>
    Conflict,

    /// <summary>Any other refusal (validation, server error, offline).</summary>
    Rejected,

    /// <summary>The access/refresh token was rejected; the app must sign in again.</summary>
    SignedOut,
}

public sealed record SyncWriteResult(
    SyncWriteStatus Status,
    ChangeDto? Change,
    SyncConflictCurrentDto? Current,
    SyncError? Error)
{
    public bool Succeeded => Status is SyncWriteStatus.Applied or SyncWriteStatus.Replayed;
}

/// <summary>
/// Write half of <c>/v1/sync</c>. Server 0.1.1 accepts exactly one change per
/// request; there is no batch endpoint. The local outbox (phase 2) drains it
/// one row at a time so an idempotency key always maps to one request.
/// </summary>
public sealed class SyncWriteClient
{
    private readonly IMosaicApi _api;

    public SyncWriteClient(IMosaicApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public async Task<SyncWriteResult> PushAsync(
        ChangePushRequestDto change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        var result = await _api.PostAsync<SyncPushResponseDto>(
                "v1/sync/changes",
                change,
                authenticated: true,
                retryTransient: false,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            if (result.Value?.Change is not { } stored)
            {
                return new SyncWriteResult(
                    SyncWriteStatus.Rejected,
                    null,
                    null,
                    new SyncError(SyncErrorKind.Protocol, LocalTransportCodes.Protocol));
            }

            return new SyncWriteResult(
                result.Value.Replayed ? SyncWriteStatus.Replayed : SyncWriteStatus.Applied,
                stored,
                null,
                null);
        }

        if (result.ErrorCode == WireErrorCodes.SyncConflict)
        {
            var current = result.Error?.DetailsAs<SyncConflictDetailsDto>()?.Current;
            return new SyncWriteResult(
                SyncWriteStatus.Conflict,
                null,
                current,
                new SyncError(SyncErrorKind.Protocol, WireErrorCodes.SyncConflict));
        }

        var error = SyncErrorMapper.FromApi(result.StatusCode, result.Error);
        return new SyncWriteResult(
            error.Kind == SyncErrorKind.SessionExpired ? SyncWriteStatus.SignedOut : SyncWriteStatus.Rejected,
            null,
            null,
            error);
    }
}
