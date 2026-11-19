using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Auth;

public sealed class DeviceFlowClient
{
    private readonly MosaicApiClient _api;

    public DeviceFlowClient(MosaicApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    /// <summary>Phone-initiated flow: creates a code the phone itself polls.</summary>
    public async Task<ApiResult<DeviceStartResponseDto>> StartAsync(
        string deviceLabel,
        string platform,
        string? appVersion,
        CancellationToken cancellationToken = default)
    {
        var result = await _api.PostAsync<DeviceStartResponseDto>(
                "v1/device/start",
                new DeviceStartRequestDto
                {
                    DeviceLabel = deviceLabel,
                    Platform = platform,
                    AppVersion = appVersion,
                },
                authenticated: false,
                retryTransient: false,
                cancellationToken)
            .ConfigureAwait(false);

        // Older builds omit verificationUriComplete; fall back to the plain URI
        // rather than constructing a ?code= query the server did not hand out.
        return result.IsSuccess
               && result.Value is { } start
               && string.IsNullOrWhiteSpace(start.VerificationUriComplete)
            ? result with { Value = start with { VerificationUriComplete = start.VerificationUri } }
            : result;
    }

    public Task<ApiResult<DevicePollResponseDto>> PollAsync(
        string deviceCode,
        CancellationToken cancellationToken = default) =>
        _api.PostAsync<DevicePollResponseDto>(
            "v1/device/poll",
            new DevicePollRequestDto(deviceCode),
            authenticated: false,
            retryTransient: false,
            cancellationToken);

    /// <summary>Approval side: what the desktop asked to authorize (signed-in phone only).</summary>
    public Task<ApiResult<DeviceInfoDto>> InspectAsync(
        string userCode,
        CancellationToken cancellationToken = default) =>
        _api.GetAsync<DeviceInfoDto>(
            $"v1/device/info?userCode={Uri.EscapeDataString(userCode)}",
            authenticated: true,
            cancellationToken: cancellationToken);

    public Task<ApiResult<SimpleStatusDto>> ApproveAsync(
        string userCode,
        CancellationToken cancellationToken = default) =>
        _api.PostAsync<SimpleStatusDto>(
            "v1/device/approve",
            new DeviceCodeRequestDto(userCode),
            authenticated: true,
            retryTransient: false,
            cancellationToken);

    public Task<ApiResult<SimpleStatusDto>> DenyAsync(
        string userCode,
        CancellationToken cancellationToken = default) =>
        _api.PostAsync<SimpleStatusDto>(
            "v1/device/deny",
            new DeviceCodeRequestDto(userCode),
            authenticated: true,
            retryTransient: false,
            cancellationToken);
}

public sealed record DeviceFlowResult(DeviceFlowState State, SessionDto? Session, AuthError? Error);

/// <summary>
/// One polling run. Not reusable across codes. The app cancels it when the
/// screen leaves the foreground and calls <see cref="RunAsync"/> again to
/// resume; the code and expiry are preserved.
/// </summary>
public sealed class DeviceFlowPollingSession
{
    private const int MaxDiagnostics = 20;

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly DeviceFlowClient _client;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private readonly List<string> _diagnostics = [];
    private CancellationTokenSource? _runCts;
    private DeviceFlowState _state = DeviceFlowState.Pending;
    private TimeSpan _interval;

    public DeviceFlowPollingSession(
        DeviceFlowClient client,
        DeviceStartResponseDto start,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(start);
        if (string.IsNullOrWhiteSpace(start.DeviceCode)
            || string.IsNullOrWhiteSpace(start.UserCode))
        {
            throw new ArgumentException("device start response is incomplete", nameof(start));
        }

        _client = client;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((duration, ct) => Task.Delay(duration, ct));
        DeviceCode = start.DeviceCode;
        UserCode = start.UserCode;
        VerificationUri = start.VerificationUri ?? string.Empty;
        VerificationUriComplete = start.VerificationUriComplete;
        _interval = TimeSpan.FromSeconds(Math.Clamp(start.Interval, 1, 60));
        ExpiresAt = _time.GetUtcNow().Add(TimeSpan.FromSeconds(Math.Max(start.ExpiresIn, 60)));
    }

    public event EventHandler<DeviceFlowState>? StateChanged;

    public string DeviceCode { get; }

    public string UserCode { get; }

    public string VerificationUri { get; }

    public string? VerificationUriComplete { get; }

    /// <summary>
    /// What the user should open. Server 0.1.1 hands out a complete URI that
    /// already carries the code; the plain URI is only a fallback.
    /// </summary>
    public string AuthorizationUri =>
        !string.IsNullOrWhiteSpace(VerificationUriComplete)
            ? VerificationUriComplete
            : VerificationUri;

    /// <summary>Display form, e.g. <c>BAAD-C832</c>.</summary>
    public string DisplayUserCode => AuthInputRules.FormatCodeForDisplay(UserCode);

    public DateTimeOffset ExpiresAt { get; }

    public TimeSpan Interval
    {
        get
        {
            lock (_gate)
            {
                return _interval;
            }
        }
    }

    public DeviceFlowState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _runCts is not null;
            }
        }
    }

    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return [.. _diagnostics];
            }
        }
    }

    /// <summary>Stops the loop without changing the flow state (background pause).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _runCts?.Cancel();
        }
    }

    public async Task<DeviceFlowResult> RunAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linked;
        lock (_gate)
        {
            if (_runCts is not null)
            {
                throw new InvalidOperationException("a polling run is already active");
            }

            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runCts = linked;
        }

        try
        {
            while (true)
            {
                if (State is not (DeviceFlowState.Pending or DeviceFlowState.Unknown))
                {
                    return Terminal();
                }

                if (_time.GetUtcNow() >= ExpiresAt)
                {
                    SetState(DeviceFlowState.Expired);
                    return new DeviceFlowResult(DeviceFlowState.Expired, null, null);
                }

                await _delay(Interval, linked.Token).ConfigureAwait(false);

                if (_time.GetUtcNow() >= ExpiresAt)
                {
                    SetState(DeviceFlowState.Expired);
                    return new DeviceFlowResult(DeviceFlowState.Expired, null, null);
                }

                var poll = await _client.PollAsync(DeviceCode, linked.Token).ConfigureAwait(false);
                if (poll.StatusCode == 429 || poll.ErrorCode == WireErrorCodes.DeviceRateLimited)
                {
                    SlowDown(poll.RetryAfter, poll.Value?.Interval);
                    AddDiagnostic(poll.ErrorCode ?? WireErrorCodes.DeviceRateLimited);
                    continue;
                }

                if (!poll.IsSuccess)
                {
                    // Transport/server hiccup: keep the code and retry at the
                    // current cadence until the code expires.
                    AddDiagnostic(poll.ErrorCode ?? $"http.{poll.StatusCode}");
                    continue;
                }

                var status = poll.Value?.Status;
                if (string.Equals(status, DeviceFlowStatus.SlowDown, StringComparison.OrdinalIgnoreCase))
                {
                    // 200 + status=slow_down carries the server's requested
                    // interval; never poll faster than that.
                    SlowDown(null, poll.Value?.Interval);
                    continue;
                }

                switch (DeviceFlowStateParser.Parse(status))
                {
                    case DeviceFlowState.Pending:
                        continue;
                    case DeviceFlowState.Approved:
                        var session = poll.Value?.Session;
                        if (session is null
                            || string.IsNullOrWhiteSpace(session.AccessToken)
                            || string.IsNullOrWhiteSpace(session.RefreshToken))
                        {
                            SetState(DeviceFlowState.Unknown);
                            return new DeviceFlowResult(
                                DeviceFlowState.Unknown,
                                null,
                                new AuthError(AuthErrorKind.Protocol, LocalTransportCodes.Protocol));
                        }

                        SetState(DeviceFlowState.Approved);
                        return new DeviceFlowResult(DeviceFlowState.Approved, session, null);
                    case DeviceFlowState.Denied:
                        SetState(DeviceFlowState.Denied);
                        return new DeviceFlowResult(DeviceFlowState.Denied, null, null);
                    case DeviceFlowState.Expired:
                        SetState(DeviceFlowState.Expired);
                        return new DeviceFlowResult(DeviceFlowState.Expired, null, null);
                    case DeviceFlowState.Consumed:
                        SetState(DeviceFlowState.Consumed);
                        return new DeviceFlowResult(DeviceFlowState.Consumed, null, null);
                    default:
                        AddDiagnostic($"device.status_unknown:{status}");
                        continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Cleanup();
            return new DeviceFlowResult(State, null, AuthError.Cancelled);
        }
        finally
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _runCts;
            _runCts = null;
        }

        cts?.Dispose();
    }

    private DeviceFlowResult Terminal()
    {
        var state = State;
        return new DeviceFlowResult(state, null, null);
    }

    private void SlowDown(TimeSpan? retryAfter, long? serverInterval)
    {
        lock (_gate)
        {
            // RFC 8628: increase by 5 seconds; the server's advertised interval
            // is a floor, never a reason to poll faster.
            var next = _interval + TimeSpan.FromSeconds(5);
            if (serverInterval is > 0)
            {
                next = TimeSpan.FromSeconds(Math.Max(next.TotalSeconds, serverInterval.Value));
            }

            if (retryAfter is { } wait && wait > next)
            {
                next = wait;
            }

            _interval = TimeSpan.FromSeconds(Math.Min(next.TotalSeconds, 60));
        }
    }

    private void AddDiagnostic(string value)
    {
        lock (_gate)
        {
            if (_diagnostics.Count >= MaxDiagnostics)
            {
                _diagnostics.RemoveAt(0);
            }

            _diagnostics.Add(value);
        }
    }

    private void SetState(DeviceFlowState state)
    {
        lock (_gate)
        {
            _state = state;
        }

        StateChanged?.Invoke(this, state);
    }
}

/// <summary>The "approve this desktop" side, after the user typed the code.</summary>
public sealed class DeviceApprovalService
{
    private readonly DeviceFlowClient _client;

    public DeviceApprovalService(DeviceFlowClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task<AuthResult<DeviceInfoDto>> InspectAsync(
        string userCode,
        CancellationToken cancellationToken = default)
    {
        var normalized = DeviceUserCodes.Normalize(userCode);
        if (normalized is null)
        {
            return AuthResult<DeviceInfoDto>.Fail(
                new AuthError(AuthErrorKind.DeviceCodeInvalid, "device.invalid_code"));
        }

        var result = await _client.InspectAsync(normalized, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? AuthResult<DeviceInfoDto>.Ok(result.Value ?? new DeviceInfoDto())
            : AuthResult<DeviceInfoDto>.Fail(result.StatusCode, result.Error, result.RetryAfter);
    }

    public Task<AuthResult> ApproveAsync(string userCode, CancellationToken cancellationToken = default) =>
        ActAsync(userCode, approve: true, cancellationToken);

    public Task<AuthResult> DenyAsync(string userCode, CancellationToken cancellationToken = default) =>
        ActAsync(userCode, approve: false, cancellationToken);

    private async Task<AuthResult> ActAsync(string userCode, bool approve, CancellationToken cancellationToken)
    {
        var normalized = DeviceUserCodes.Normalize(userCode);
        if (normalized is null)
        {
            return AuthResult.Fail(new AuthError(AuthErrorKind.DeviceCodeInvalid, "device.invalid_code"));
        }

        var result = approve
            ? await _client.ApproveAsync(normalized, cancellationToken).ConfigureAwait(false)
            : await _client.DenyAsync(normalized, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? AuthResult.Ok()
            : AuthResult.Fail(result.StatusCode, result.Error, result.RetryAfter);
    }
}
