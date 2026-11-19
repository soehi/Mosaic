using Mosaic.Auth.Contracts;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;

namespace Mosaic.Auth.Core.Internal;

internal sealed class AuthService(
    IAuthTransport transport,
    IAuthSecretStore secrets,
    ISettingsApi settings,
    TimeProvider clock,
    IDiagnosticSink diagnostics) : IAuthApi
{
    private static readonly IReadOnlyDictionary<string, string> KnownMessages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AuthErrorCodes.InvalidCredentials] = "邮箱或密码不正确。",
        [AuthErrorCodes.EmailTaken] = "该邮箱已注册。",
        [AuthErrorCodes.CodeInvalid] = "验证码不正确。",
        [AuthErrorCodes.CodeExpired] = "验证码已过期，请重新获取。",
        [AuthErrorCodes.RateLimited] = "请求过于频繁，请稍后再试。",
        [AuthErrorCodes.RegistrationClosed] = "服务器当前未开放注册。",
        [AuthErrorCodes.PasswordTooWeak] = "密码强度不足，请使用至少 10 个字符。",
        [AuthErrorCodes.TokenExpired] = "登录状态已过期，请重新登录。",
        [AuthErrorCodes.TokenInvalid] = "登录状态无效，请重新登录。",
        [AuthErrorCodes.DeviceCodeInvalid] = "设备授权码无效或已过期。",
        [AuthErrorCodes.NetworkUnavailable] = "无法连接服务器，请检查网络与服务器地址。",
        [AuthErrorCodes.ServerUntrusted] = "服务器地址必须使用 HTTPS。"
    };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private AuthState _state = AuthState.SignedOut();
    private AuthSessionDto? _session;
    private DateTimeOffset _accessExpiresAtUtc;
    private AuthSecrets? _stored;
    private Guid _deviceId = Guid.NewGuid();
    private DeviceAuthorizationDto? _device;
    private DateTimeOffset _deviceExpiresAtUtc;
    private bool _activated;

    public event EventHandler? StateChanged;

    public AuthState State => Volatile.Read(ref _state);
    public Guid DeviceId => _deviceId;

    internal async Task ActivateAsync(CancellationToken cancellationToken)
    {
        Uri? server = null;
        var values = await settings.GetValuesAsync(cancellationToken).ConfigureAwait(false);
        if (values.IsSuccess &&
            values.Value.GetValueOrDefault(AuthSettingsSource.ServerUrl) is string text &&
            AuthValidation.TryParseServer(text, out var parsed))
            server = parsed;
        var loaded = await secrets.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsSuccess && loaded.Value is { } stored)
        {
            _deviceId = stored.DeviceId == Guid.Empty ? Guid.NewGuid() : stored.DeviceId;
            // Restart never contacts the network: a stored refresh token is enough to show the
            // account offline; the token is only refreshed when a request actually needs it.
            if (server is not null && SameOrigin(stored.Server, server))
            {
                _stored = stored;
                _activated = true;
                Volatile.Write(ref _state, new(AuthStatus.SignedIn, new(stored.UserId, stored.Email, stored.DisplayName), server));
                return;
            }
        }
        _activated = true;
        Volatile.Write(ref _state, AuthState.SignedOut(server));
    }

    internal Task DeactivateAsync()
    {
        _activated = false;
        Volatile.Write(ref _state, AuthState.SignedOut(State.Server));
        _session = null;
        _stored = null;
        _device = null;
        return Task.CompletedTask;
    }

    // Called by the settings pipeline whenever the server address is applied (startup snapshot,
    // settings page edits, reset). Credentials are bound to the server origin, so a different
    // origin drops them instead of sending one server's refresh token to another.
    internal async Task ApplyServerSettingAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        var server = values.GetValueOrDefault(AuthSettingsSource.ServerUrl) is string text && AuthValidation.TryParseServer(text, out var parsed)
            ? parsed
            : null;
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_activated) return;
            if (_stored is not null && server is not null && SameOrigin(_stored.Server, server))
            {
                UpdateState(new(AuthStatus.SignedIn, new(_stored.UserId, _stored.Email, _stored.DisplayName), server));
                return;
            }
            if (_stored is not null)
            {
                await secrets.ClearAsync(cancellationToken).ConfigureAwait(false);
                _stored = null;
                _session = null;
            }
            UpdateState(AuthState.SignedOut(server));
        }
        finally { _operations.Release(); }
    }

    public async Task<OperationResult<AuthPolicyDto>> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (State.Server is not { } server) return Failure<AuthPolicyDto>(AuthErrorCodes.ServerRequired, "请先填写服务器地址。", "Server");
        var policy = await transport.GetPolicyAsync(server, cancellationToken).ConfigureAwait(false);
        return policy.IsSuccess
            ? OperationResult<AuthPolicyDto>.Success(policy.Value with { ServerName = policy.Value.ServerName?.Trim() ?? "" })
            : OperationResult<AuthPolicyDto>.Failure(Map(policy.Error!));
    }

    public async Task<OperationResult> SetServerAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        if (!AuthValidation.TryParseServer(serverUrl, out var server))
            return Failure(AuthErrorCodes.ServerUntrusted, "服务器地址无效，必须使用 https:// 开头的地址。", "Server");
        var saved = await SaveSettingsAsync(new Dictionary<string, object?> { [AuthSettingsSource.ServerUrl] = AuthValidation.FormatServer(server!) }, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess) return saved;
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stored is not null && !SameOrigin(_stored.Server, server!))
            {
                await secrets.ClearAsync(cancellationToken).ConfigureAwait(false);
                _stored = null;
                _session = null;
            }
            UpdateState(_stored is null
                ? AuthState.SignedOut(server)
                : new(AuthStatus.SignedIn, new(_stored.UserId, _stored.Email, _stored.DisplayName), server));
            return OperationResult.Success();
        }
        finally { _operations.Release(); }
    }

    public Task<OperationResult> RequestEmailCodeAsync(string email, AuthEmailCodePurpose purpose, CancellationToken cancellationToken = default) =>
        WithServerAsync(async server =>
        {
            if (!AuthValidation.IsValidEmail(email)) return Failure(ErrorCodes.ValidationFailed, "请输入有效的邮箱地址。", "Email");
            var result = await transport.SendEmailCodeAsync(server, AuthValidation.NormalizeEmail(email), purpose, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(Map(result.Error!));
        }, cancellationToken);

    public Task<OperationResult> RegisterAsync(string email, string code, string password, CancellationToken cancellationToken = default) =>
        WithServerAsync(async server =>
        {
            if (Validation(email, code, password, confirmPassword: password) is { } error) return OperationResult.Failure(error);
            var result = await transport.RegisterAsync(server, AuthValidation.NormalizeEmail(email), password, code, cancellationToken).ConfigureAwait(false);
            return await CompleteSignInAsync(result, cancellationToken).ConfigureAwait(false);
        }, cancellationToken, stateChanging: true);

    public Task<OperationResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default) =>
        WithServerAsync(async server =>
        {
            if (!AuthValidation.IsValidEmail(email)) return Failure(ErrorCodes.ValidationFailed, "请输入有效的邮箱地址。", "Email");
            if (!AuthValidation.IsValidPassword(password)) return ValidationFailure("Password");
            var result = await transport.LoginAsync(server, AuthValidation.NormalizeEmail(email), password, cancellationToken).ConfigureAwait(false);
            return await CompleteSignInAsync(result, cancellationToken).ConfigureAwait(false);
        }, cancellationToken, stateChanging: true);

    public Task<OperationResult> LoginWithCodeAsync(string email, string code, CancellationToken cancellationToken = default) =>
        WithServerAsync(async server =>
        {
            if (!AuthValidation.IsValidEmail(email)) return Failure(ErrorCodes.ValidationFailed, "请输入有效的邮箱地址。", "Email");
            if (!AuthValidation.IsValidEmailCode(code)) return ValidationFailure("Code");
            var result = await transport.LoginWithCodeAsync(server, AuthValidation.NormalizeEmail(email), code, cancellationToken).ConfigureAwait(false);
            return await CompleteSignInAsync(result, cancellationToken).ConfigureAwait(false);
        }, cancellationToken, stateChanging: true);

    public Task<OperationResult> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) =>
        WithServerAsync(async server =>
        {
            if (!AuthValidation.IsValidEmail(email)) return Failure(ErrorCodes.ValidationFailed, "请输入有效的邮箱地址。", "Email");
            var result = await transport.ForgotPasswordAsync(server, AuthValidation.NormalizeEmail(email), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(Map(result.Error!));
        }, cancellationToken);

    public Task<OperationResult> ResetPasswordAsync(string email, string code, string newPassword, CancellationToken cancellationToken = default) =>
        WithServerAsync(async server =>
        {
            if (!AuthValidation.IsValidEmail(email)) return Failure(ErrorCodes.ValidationFailed, "请输入有效的邮箱地址。", "Email");
            if (!AuthValidation.IsValidEmailCode(code)) return ValidationFailure("Code");
            if (!AuthValidation.IsValidPassword(newPassword)) return ValidationFailure("Password");
            var result = await transport.ResetPasswordAsync(server, AuthValidation.NormalizeEmail(email), code, newPassword, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(Map(result.Error!));
        }, cancellationToken);

    public async Task<OperationResult<DeviceAuthorizationDto>> StartDeviceAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        if (State.Server is not { } server) return Failure<DeviceAuthorizationDto>(AuthErrorCodes.ServerRequired, "请先填写服务器地址。", "Server");
        var started = await transport.StartDeviceAuthorizationAsync(server, cancellationToken).ConfigureAwait(false);
        if (!started.IsSuccess) return OperationResult<DeviceAuthorizationDto>.Failure(Map(started.Error!));
        var authorization = started.Value;
        if (string.IsNullOrWhiteSpace(authorization.DeviceCode) || string.IsNullOrWhiteSpace(authorization.UserCode))
            return Failure<DeviceAuthorizationDto>(ErrorCodes.InternalError, "服务器返回的设备授权信息无效。");
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _device = authorization;
            _deviceExpiresAtUtc = clock.GetUtcNow().AddSeconds(Math.Max(30, authorization.ExpiresInSeconds));
            return OperationResult<DeviceAuthorizationDto>.Success(authorization);
        }
        finally { _operations.Release(); }
    }

    public async Task<OperationResult<DeviceAuthorizationStatus>> PollDeviceAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        var pending = _device;
        if (pending is null) return Failure<DeviceAuthorizationStatus>(AuthErrorCodes.DeviceFlowNotStarted, "当前没有等待确认的设备授权。");
        if (clock.GetUtcNow() >= _deviceExpiresAtUtc)
        {
            _device = null;
            return OperationResult<DeviceAuthorizationStatus>.Success(DeviceAuthorizationStatus.Expired);
        }
        if (State.Server is not { } server) return Failure<DeviceAuthorizationStatus>(AuthErrorCodes.ServerRequired, "请先填写服务器地址。");
        var polled = await transport.PollDeviceAuthorizationAsync(server, pending.DeviceCode, cancellationToken).ConfigureAwait(false);
        if (!polled.IsSuccess) return OperationResult<DeviceAuthorizationStatus>.Failure(Map(polled.Error!));
        var status = polled.Value.Status;
        if (status == DeviceAuthorizationStatus.Approved)
        {
            if (polled.Value.Session is null)
                return Failure<DeviceAuthorizationStatus>(ErrorCodes.InternalError, "服务器未返回登录凭据。");
            var signedIn = await CompleteSignInAsync(OperationResult<AuthSessionDto>.Success(polled.Value.Session), cancellationToken).ConfigureAwait(false);
            if (!signedIn.IsSuccess) return OperationResult<DeviceAuthorizationStatus>.Failure(signedIn.Error!);
            _device = null;
            return OperationResult<DeviceAuthorizationStatus>.Success(DeviceAuthorizationStatus.Approved);
        }
        if (status != DeviceAuthorizationStatus.Pending) _device = null;
        return OperationResult<DeviceAuthorizationStatus>.Success(status);
    }

    public async Task<OperationResult> CancelDeviceAuthorizationAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try { _device = null; return OperationResult.Success(); }
        finally { _operations.Release(); }
    }

    public async Task<OperationResult> LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var server = State.Server;
            var session = _session;
            if (session is not null && server is not null)
            {
                // Revocation is best effort: a network failure must not keep the local credential.
                await transport.LogoutAsync(server, session.AccessToken, session.RefreshToken, cancellationToken).ConfigureAwait(false);
            }
            var cleared = await secrets.ClearAsync(cancellationToken).ConfigureAwait(false);
            _session = null;
            _stored = null;
            _device = null;
            UpdateState(AuthState.SignedOut(server));
            return cleared.IsSuccess ? OperationResult.Success() : Failure(AuthErrorCodes.SecretsUnavailable, "无法清除本机保存的登录凭据。");
        }
        finally { _operations.Release(); }
    }

    public async Task<OperationResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null || _stored is null || State.Server is not { } server)
            return Failure<string>(AuthErrorCodes.TokenInvalid, "尚未登录。");
        if (clock.GetUtcNow() < _accessExpiresAtUtc) return OperationResult<string>.Success(session.AccessToken);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session = _session;
            if (session is null || _stored is null) return Failure<string>(AuthErrorCodes.TokenInvalid, "尚未登录。");
            if (clock.GetUtcNow() < _accessExpiresAtUtc) return OperationResult<string>.Success(session.AccessToken);
            var refreshed = await transport.RefreshAsync(server, _stored.RefreshToken, cancellationToken).ConfigureAwait(false);
            if (!refreshed.IsSuccess)
            {
                var error = Map(refreshed.Error!);
                if (error.Code is AuthErrorCodes.TokenInvalid or AuthErrorCodes.TokenExpired)
                {
                    await secrets.ClearAsync(cancellationToken).ConfigureAwait(false);
                    _session = null;
                    _stored = null;
                    UpdateState(AuthState.SignedOut(server));
                }
                return OperationResult<string>.Failure(error);
            }
            var completed = await CompleteSignInAsync(refreshed, cancellationToken).ConfigureAwait(false);
            return completed.IsSuccess ? OperationResult<string>.Success(_session!.AccessToken) : OperationResult<string>.Failure(completed.Error!);
        }
        finally { _operations.Release(); }
    }

    private async Task<OperationResult> CompleteSignInAsync(OperationResult<AuthSessionDto> result, CancellationToken cancellationToken)
    {
        if (!result.IsSuccess) return OperationResult.Failure(Map(result.Error!));
        var session = result.Value;
        if (string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.RefreshToken) ||
            session.Account is null || session.Account.UserId == Guid.Empty || string.IsNullOrWhiteSpace(session.Account.Email))
            return Failure(ErrorCodes.InternalError, "服务器返回的登录信息无效。");
        if (State.Server is not { } server) return Failure(AuthErrorCodes.ServerRequired, "请先填写服务器地址。");
        var stored = new AuthSecrets(session.RefreshToken, _deviceId, session.Account.UserId, session.Account.Email, session.Account.DisplayName, server);
        var saved = await secrets.SaveAsync(stored, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
            return Failure(AuthErrorCodes.SecretsUnavailable, "无法安全保存登录凭据，登录未完成。");
        _stored = stored;
        _session = session;
        _accessExpiresAtUtc = clock.GetUtcNow().AddSeconds(Math.Max(30, session.ExpiresInSeconds - 60));
        UpdateState(new(AuthStatus.SignedIn, session.Account, server));
        return OperationResult.Success();
    }

    private async Task<OperationResult> WithServerAsync(Func<Uri, Task<OperationResult>> operation, CancellationToken cancellationToken, bool stateChanging = false)
    {
        if (State.Server is not { } server) return Failure(AuthErrorCodes.ServerRequired, "请先填写服务器地址。", "Server");
        if (!stateChanging) return await operation(server).ConfigureAwait(false);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation(server).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    private OperationError? Validation(string email, string code, string password, string? confirmPassword)
    {
        if (!AuthValidation.IsValidEmail(email)) return new(ErrorCodes.ValidationFailed, "请输入有效的邮箱地址。", "Email");
        if (!AuthValidation.IsValidEmailCode(code)) return new(ErrorCodes.ValidationFailed, "请输入 6 位数字验证码。", "Code");
        if (!AuthValidation.IsValidPassword(password)) return new(ErrorCodes.ValidationFailed, "密码需为 10–256 个字符。", "Password");
        if (confirmPassword is not null && !string.Equals(confirmPassword, password, StringComparison.Ordinal))
            return new(ErrorCodes.ValidationFailed, "两次输入的密码不一致。", "ConfirmPassword");
        return null;
    }

    private static OperationResult ValidationFailure(string field) => OperationResult.Failure(field switch
    {
        "Code" => new(ErrorCodes.ValidationFailed, "请输入 6 位数字验证码。", "Code"),
        "Password" => new(ErrorCodes.ValidationFailed, "密码需为 10–256 个字符。", "Password"),
        _ => new(ErrorCodes.ValidationFailed, "输入无效。", field)
    });

    private static OperationError Map(OperationError error) =>
        KnownMessages.TryGetValue(error.Code, out var message)
            ? error with { Message = message, MessageKey = "Auth.Error." + error.Code }
            : error;

    private static OperationResult Failure(string code, string message, string? field = null) =>
        OperationResult.Failure(new(code, message, field));

    private static OperationResult<T> Failure<T>(string code, string message, string? field = null) =>
        OperationResult<T>.Failure(new(code, message, field));

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.GetLeftPart(UriPartial.Authority), right.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private void UpdateState(AuthState state)
    {
        Volatile.Write(ref _state, state);
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { diagnostics.Report(Guid.NewGuid().ToString("N"), ex); }
    }

    private async Task<OperationResult> SaveSettingsAsync(IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken)
    {
        var session = await settings.BeginEditSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess) return OperationResult.Failure(session.Error!);
        await using var edit = session.Value;
        var preview = await edit.PreviewAsync(changes, 1, cancellationToken).ConfigureAwait(false);
        if (!preview.IsSuccess) return OperationResult.Failure(preview.Error!);
        if (preview.Value.Errors.Count > 0)
            return OperationResult.Failure(preview.Value.Errors.First().Value);
        var commit = await edit.CommitAsync(1, cancellationToken).ConfigureAwait(false);
        return commit.IsSuccess ? OperationResult.Success() : OperationResult.Failure(commit.Error!);
    }
}
