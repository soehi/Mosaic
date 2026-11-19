using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Mosaic.Auth.Contracts;
using Mosaic.Contracts;

namespace Mosaic.Auth.Http;

/// <summary>
/// Wire adapter for the server's <c>/v1</c> account endpoints. Secrets exist in memory only for
/// the duration of a call; no request or response body is ever written to configuration or logs.
/// </summary>
public sealed class HttpAuthTransport : IAuthTransport
{
    private const string RegisterPath = "v1/auth/register";
    private const string LoginPath = "v1/auth/login";
    private const string LoginCodePath = "v1/auth/login/code";
    private const string RefreshPath = "v1/auth/refresh";
    private const string LogoutPath = "v1/auth/logout";
    private const string ForgotPasswordPath = "v1/auth/forgot-password";
    private const string ResetPasswordPath = "v1/auth/reset-password";
    private const string PolicyPath = "v1/auth/policy";
    private const string EmailCodePath = "v1/auth/email-code";
    private const string MePath = "v1/auth/me";
    private const string SessionsPath = "v1/auth/sessions";
    private const string SessionsRevokeAllPath = "v1/auth/sessions/revoke-all";
    private const string AccountDeletePath = "v1/auth/account/delete";
    private const string DeviceStartPath = "v1/device/start";
    private const string DevicePollPath = "v1/device/poll";
    private const string DeviceLabel = "Mosaic 桌面客户端";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly TimeProvider _clock;
    private readonly bool _allowInsecureLoopback;

    public HttpAuthTransport(HttpClient client, TimeProvider? clock = null, bool allowInsecureLoopback = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _clock = clock ?? TimeProvider.System;
        _allowInsecureLoopback = allowInsecureLoopback;
    }

    public async Task<OperationResult<AuthPolicyDto>> GetPolicyAsync(Uri server, CancellationToken cancellationToken = default)
    {
        return await ReadAsync(HttpMethod.Get, server, PolicyPath, null, null, root =>
        {
            var serverName = Text(root, "serverName") ?? "";
            return OperationResult<AuthPolicyDto>.Success(new(
                Flag(root, "registrationOpen"),
                root.TryGetProperty("requireEmailVerification", out var verification) && verification.ValueKind == JsonValueKind.True,
                serverName));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> SendEmailCodeAsync(Uri server, string email, AuthEmailCodePurpose purpose, CancellationToken cancellationToken = default)
    {
        var body = new { email, purpose = PurposeWire(purpose) };
        return await WriteAsync(HttpMethod.Post, server, EmailCodePath, body, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<AuthSessionDto>> RegisterAsync(Uri server, string email, string password, string code, CancellationToken cancellationToken = default)
    {
        var body = new { email, password, code, deviceLabel = DeviceLabel };
        return await ReadAsync(HttpMethod.Post, server, RegisterPath, body, null, ReadSession, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<AuthSessionDto>> LoginAsync(Uri server, string email, string password, CancellationToken cancellationToken = default)
    {
        var body = new { email, password, deviceLabel = DeviceLabel };
        return await ReadAsync(HttpMethod.Post, server, LoginPath, body, null, ReadSession, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<AuthSessionDto>> LoginWithCodeAsync(Uri server, string email, string code, CancellationToken cancellationToken = default)
    {
        var body = new { email, code, deviceLabel = DeviceLabel };
        return await ReadAsync(HttpMethod.Post, server, LoginCodePath, body, null, ReadSession, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<AuthSessionDto>> RefreshAsync(Uri server, string refreshToken, CancellationToken cancellationToken = default)
    {
        var body = new { refreshToken };
        return await ReadAsync(HttpMethod.Post, server, RefreshPath, body, null, ReadSession, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> LogoutAsync(Uri server, string accessToken, string refreshToken, CancellationToken cancellationToken = default)
    {
        var body = new { refreshToken, allDevices = false };
        return await WriteAsync(HttpMethod.Post, server, LogoutPath, body, accessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> ForgotPasswordAsync(Uri server, string email, CancellationToken cancellationToken = default)
    {
        var body = new { email };
        return await WriteAsync(HttpMethod.Post, server, ForgotPasswordPath, body, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> ResetPasswordAsync(Uri server, string email, string code, string password, CancellationToken cancellationToken = default)
    {
        var body = new { email, code, newPassword = password };
        return await WriteAsync(HttpMethod.Post, server, ResetPasswordPath, body, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<AuthAccountDto>> GetAccountAsync(Uri server, string accessToken, CancellationToken cancellationToken = default)
    {
        return await ReadAsync(HttpMethod.Get, server, MePath, null, accessToken, root =>
            TryReadAccount(root, out var account)
                ? OperationResult<AuthAccountDto>.Success(account)
                : Invalid<AuthAccountDto>("服务器返回的账号信息无效。"), cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<DeviceAuthorizationDto>> StartDeviceAuthorizationAsync(Uri server, string appVersion, CancellationToken cancellationToken = default)
    {
        var body = new { deviceLabel = DeviceLabel, platform = "windows", appVersion };
        return await ReadAsync(HttpMethod.Post, server, DeviceStartPath, body, null, root =>
        {
            var deviceCode = Text(root, "deviceCode");
            var userCode = Text(root, "userCode");
            if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(userCode))
                return Invalid<DeviceAuthorizationDto>("服务器返回的设备授权信息无效。");
            var verification = Text(root, "verificationUri");
            var baseUri = !string.IsNullOrWhiteSpace(verification) && Uri.TryCreate(verification, UriKind.Absolute, out var parsed)
                ? parsed
                : new Uri(server, "device");
            var completeText = Text(root, "verificationUriComplete");
            Uri? complete = null;
            if (!string.IsNullOrWhiteSpace(completeText) && Uri.TryCreate(completeText, UriKind.Absolute, out var parsedComplete))
                complete = parsedComplete;
            else if (Uri.TryCreate(server, "device?user_code=" + Uri.EscapeDataString(userCode), out var constructed))
                complete = constructed;
            return OperationResult<DeviceAuthorizationDto>.Success(new(
                deviceCode,
                userCode,
                baseUri,
                complete,
                Math.Max(30, Integer(root, "expiresIn", 600)),
                Math.Max(2, Integer(root, "interval", 5))));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<DevicePollDto>> PollDeviceAuthorizationAsync(Uri server, string deviceCode, CancellationToken cancellationToken = default)
    {
        var body = new { deviceCode };
        return await ReadAsync(HttpMethod.Post, server, DevicePollPath, body, null, root =>
        {
            var statusText = Text(root, "status");
            if (string.Equals(statusText, "slow_down", StringComparison.Ordinal))
            {
                var interval = Integer(root, "interval", 2);
                return OperationResult<DevicePollDto>.Success(new(DeviceAuthorizationStatus.Pending, null, Math.Max(2, interval)));
            }
            var status = ReadStatus(statusText);
            if (status != DeviceAuthorizationStatus.Approved)
                return OperationResult<DevicePollDto>.Success(new(status, null));
            if (!root.TryGetProperty("session", out var session) || !TryReadSession(session, out var signedIn))
                return Invalid<DevicePollDto>("服务器返回的登录信息无效。");
            return OperationResult<DevicePollDto>.Success(new(DeviceAuthorizationStatus.Approved, signedIn));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<IReadOnlyList<AuthSessionInfoDto>>> ListSessionsAsync(Uri server, string accessToken, CancellationToken cancellationToken = default)
    {
        return await ReadAsync(HttpMethod.Get, server, SessionsPath, null, accessToken, root =>
        {
            var sessions = new List<AuthSessionInfoDto>();
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
                array = root;
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("sessions", out var listed) && listed.ValueKind == JsonValueKind.Array)
                array = listed;
            else
                return Invalid<IReadOnlyList<AuthSessionInfoDto>>("服务器返回的会话列表无效。");
            foreach (var entry in array.EnumerateArray())
            {
                var id = Text(entry, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var createdAt = DateTimeOffset.TryParse(Text(entry, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)
                    ? created : DateTimeOffset.MinValue;
                var lastSeenAt = DateTimeOffset.TryParse(Text(entry, "lastSeenAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var seen)
                    ? seen : createdAt;
                var expiresAt = DateTimeOffset.TryParse(Text(entry, "expiresAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires)
                    ? expires : DateTimeOffset.MinValue;
                sessions.Add(new(id, Text(entry, "deviceLabel"), Text(entry, "platform"), createdAt, lastSeenAt, expiresAt, Flag(entry, "current")));
            }
            return OperationResult<IReadOnlyList<AuthSessionInfoDto>>.Success(sessions);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> RevokeSessionAsync(Uri server, string accessToken, string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return OperationResult.Failure(new(ErrorCodes.ValidationFailed, "会话标识无效。"));
        var path = SessionsPath + "/" + Uri.EscapeDataString(sessionId) + "/revoke";
        return await WriteAsync(HttpMethod.Post, server, path, null, accessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> RevokeAllSessionsAsync(Uri server, string accessToken, bool keepCurrent, CancellationToken cancellationToken = default)
    {
        object? body = keepCurrent ? new { keepCurrent = true } : null;
        return await WriteAsync(HttpMethod.Post, server, SessionsRevokeAllPath, body, accessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> DeleteAccountAsync(Uri server, string accessToken, string? password, string? code, CancellationToken cancellationToken = default)
    {
        var body = new { password, code };
        return await WriteAsync(HttpMethod.Post, server, AccountDeletePath, body, accessToken, cancellationToken).ConfigureAwait(false);
    }

    private OperationResult<AuthSessionDto> ReadSession(JsonElement root) =>
        TryReadSession(root, out var session) ? OperationResult<AuthSessionDto>.Success(session) : Invalid<AuthSessionDto>("服务器返回的登录信息无效。");

    private bool TryReadSession(JsonElement root, out AuthSessionDto session)
    {
        session = null!;
        var accessToken = Text(root, "accessToken");
        var refreshToken = Text(root, "refreshToken");
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken) ||
            !TryReadAccount(root, out var account))
            return false;
        var expiresIn = 900;
        if (DateTimeOffset.TryParse(Text(root, "accessTokenExpiresAt"), out var expiresAt))
        {
            var seconds = (int)(expiresAt - _clock.GetUtcNow()).TotalSeconds;
            expiresIn = Math.Clamp(seconds, 30, 86_400);
        }
        session = new(accessToken, refreshToken, expiresIn, account);
        return true;
    }

    private static bool TryReadAccount(JsonElement root, out AuthAccountDto account)
    {
        account = null!;
        if (!root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object) return false;
        var id = Text(user, "id") ?? Text(user, "userId");
        var email = Text(user, "email");
        if (!Guid.TryParse(id, out var userId) || string.IsNullOrWhiteSpace(email)) return false;
        account = new(userId, email, Text(user, "displayName"));
        return true;
    }

    private async Task<OperationResult<T>> ReadAsync<T>(
        HttpMethod method,
        Uri server,
        string path,
        object? body,
        string? accessToken,
        Func<JsonElement, OperationResult<T>> read,
        CancellationToken cancellationToken)
    {
        var sent = await SendAsync(method, server, path, body, accessToken, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess) return OperationResult<T>.Failure(sent.Error!);
        using var document = sent.Value;
        return read(document.RootElement);
    }

    private async Task<OperationResult> WriteAsync(
        HttpMethod method,
        Uri server,
        string path,
        object? body,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        var sent = await SendAsync(method, server, path, body, accessToken, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess) return OperationResult.Failure(sent.Error!);
        sent.Value.Dispose();
        return OperationResult.Success();
    }

    private async Task<OperationResult<JsonDocument>> SendAsync(
        HttpMethod method,
        Uri server,
        string path,
        object? body,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedServer(server))
            return Failure<JsonDocument>(AuthErrorCodes.ServerUntrusted, "服务器地址必须使用 HTTPS。", "Server");
        if (!Uri.TryCreate(server, path, out var endpoint))
            return Failure<JsonDocument>(AuthErrorCodes.ServerUntrusted, "服务器地址无效。", "Server");
        using var request = new HttpRequestMessage(method, endpoint);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        if (!string.IsNullOrEmpty(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return OperationResult<JsonDocument>.Failure(ReadError(response, text));
            if (text.Length == 0) return OperationResult<JsonDocument>.Success(JsonDocument.Parse("{}"));
            try
            {
                return OperationResult<JsonDocument>.Success(JsonDocument.Parse(text));
            }
            catch (JsonException)
            {
                return Failure<JsonDocument>(ErrorCodes.InternalError, "服务器响应格式无效。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return Failure<JsonDocument>(AuthErrorCodes.NetworkUnavailable, "无法连接服务器，请检查网络与服务器地址。");
        }
    }

    private bool IsAllowedServer(Uri server) =>
        server.IsAbsoluteUri && (AuthValidation.IsTrustedServer(server) || (_allowInsecureLoopback && IsLoopback(server)));

    private static bool IsLoopback(Uri server) =>
        string.Equals(server.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(server.Host, "127.0.0.1", StringComparison.Ordinal) ||
         string.Equals(server.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(server.Host, "::1", StringComparison.Ordinal));

    private static OperationError ReadError(HttpResponseMessage response, string text)
    {
        string? code = null, message = null, traceId = null;
        if (text.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    code = Text(root, "code");
                    message = Text(root, "message");
                    traceId = Text(root, "traceId");
                }
            }
            catch (JsonException) { }
        }
        var mapped = MapCode(code, response.StatusCode);
        return new(mapped, message ?? FallbackMessage(mapped), CorrelationId: traceId);
    }

    private static string MapCode(string? code, HttpStatusCode status) => code switch
    {
        "auth.invalid_credentials" => AuthErrorCodes.InvalidCredentials,
        "auth.invalid_code" or "auth.code_invalid" or "auth.code_expired" or "auth.code_attempts_exceeded" => AuthErrorCodes.CodeInvalid,
        "auth.weak_password" or "auth.password_policy" => AuthErrorCodes.PasswordTooWeak,
        "auth.rate_limited" or "device.rate_limited" => AuthErrorCodes.RateLimited,
        "auth.registration_closed" => AuthErrorCodes.RegistrationClosed,
        "auth.required" or "auth.session_expired" or "auth.invalid_refresh" or "auth.refresh_reused" => AuthErrorCodes.TokenInvalid,
        "auth.reauth_required" => AuthErrorCodes.ReauthRequired,
        "auth.session_revoked" => AuthErrorCodes.SessionRevoked,
        "device.invalid_code" => AuthErrorCodes.DeviceCodeInvalid,
        null when status == HttpStatusCode.TooManyRequests => AuthErrorCodes.RateLimited,
        null => "SERVER_ERROR",
        _ => code
    };

    private static string FallbackMessage(string code) => code switch
    {
        AuthErrorCodes.InvalidCredentials => "邮箱或密码不正确。",
        AuthErrorCodes.CodeInvalid => "验证码无效或已过期，请重新获取。",
        AuthErrorCodes.PasswordTooWeak => "密码需要 10 至 256 个字符。",
        AuthErrorCodes.RateLimited => "请求过于频繁，请稍后再试。",
        AuthErrorCodes.RegistrationClosed => "服务器当前未开放注册。",
        AuthErrorCodes.TokenInvalid => "登录已过期，请重新登录。",
        AuthErrorCodes.ReauthRequired => "需要重新验证身份后才能继续。",
        AuthErrorCodes.SessionRevoked => "会话已被撤销，请重新登录。",
        AuthErrorCodes.DeviceCodeInvalid => "设备授权码无效或已过期。",
        AuthErrorCodes.NetworkUnavailable => "无法连接服务器，请检查网络与服务器地址。",
        _ => "请求未完成，请稍后再试。"
    };

    private static DeviceAuthorizationStatus ReadStatus(string? status) => status switch
    {
        "approved" => DeviceAuthorizationStatus.Approved,
        "denied" => DeviceAuthorizationStatus.Denied,
        "expired" => DeviceAuthorizationStatus.Expired,
        "consumed" => DeviceAuthorizationStatus.Consumed,
        _ => DeviceAuthorizationStatus.Pending
    };

    private static string PurposeWire(AuthEmailCodePurpose purpose) => purpose switch
    {
        AuthEmailCodePurpose.Register => "register",
        AuthEmailCodePurpose.Login => "login",
        AuthEmailCodePurpose.Reset => "reset",
        AuthEmailCodePurpose.DeleteAccount => "delete-account",
        _ => purpose.ToString().ToLowerInvariant()
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int Integer(JsonElement element, string name, int fallback) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

    private static OperationResult<T> Invalid<T>(string message) => Failure<T>(ErrorCodes.InternalError, message);

    private static OperationResult<T> Failure<T>(string code, string message, string? field = null) =>
        OperationResult<T>.Failure(new(code, message, field));
}
