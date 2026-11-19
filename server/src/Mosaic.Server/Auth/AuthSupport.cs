using System.Diagnostics;
using System.Globalization;
using Mosaic.Server.Http;
using Mosaic.Server.Security;

namespace Mosaic.Server.Auth;

public static class AuthSupport
{
    public static async Task<SessionPrincipal> RequirePrincipalAsync(HttpContext http, SessionService sessions)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(sessions);
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            throw new ApiException(401, "auth.required", "请先登录。");
        }

        var token = header["Bearer ".Length..].Trim();
        return await sessions.AuthenticateAsync(token, http.RequestAborted).ConfigureAwait(false);
    }

    public static async Task<SessionUser> RequireUserAsync(HttpContext http, SessionService sessions) =>
        (await RequirePrincipalAsync(http, sessions).ConfigureAwait(false)).User;

    /// <summary>
    /// True when the request carries a body. Chunked uploads have no Content-Length,
    /// so callers must also look at Transfer-Encoding instead of trusting the length.
    /// </summary>
    public static bool HasBody(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.ContentLength is > 0
            || request.Headers.TransferEncoding.Count > 0;
    }

    public static string IpHash(HttpContext http, SecretHasher hasher) =>
        hasher.IpHash(http.Connection.RemoteIpAddress?.ToString());

    public static string SanitizeDeviceLabel(string? requestedLabel)
    {
        var label = (requestedLabel ?? string.Empty).Trim();
        foreach (var character in label)
        {
            if (char.IsControl(character))
            {
                return "桌面客户端";
            }
        }

        return label.Length is 0 or > 120 ? "桌面客户端" : label;
    }

    public static string SanitizeShort(string? value, int maxLength = 40)
    {
        var text = (value ?? string.Empty).Trim();
        foreach (var character in text)
        {
            if (char.IsControl(character))
            {
                return string.Empty;
            }
        }

        return text.Length > maxLength ? text[..maxLength] : text;
    }

    public static object SessionJson(SessionTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return new
        {
            accessToken = tokens.AccessToken,
            refreshToken = tokens.RefreshToken,
            accessTokenExpiresAt = TimeFormat.Iso(tokens.AccessExpiresAt),
            refreshTokenExpiresAt = TimeFormat.Iso(tokens.RefreshExpiresAt),
            expiresIn = tokens.AccessExpiresIn,
            user = UserJson(tokens.User),
            // Compatibility view for clients that read the device-account shape.
            account = AccountFields(tokens.User),
        };
    }

    public static object UserJson(SessionUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new
        {
            id = user.Id,
            email = user.Email,
            createdAt = TimeFormat.Iso(user.CreatedAt),
            emailVerifiedAt = TimeFormat.Iso(user.EmailVerifiedAt),
        };
    }

    public static object AccountJson(SessionUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new
        {
            userId = user.Id,
            email = user.Email,
            displayName = (string?)null,
            createdAt = TimeFormat.Iso(user.CreatedAt),
            emailVerifiedAt = TimeFormat.Iso(user.EmailVerifiedAt),
            user = UserJson(user),
        };
    }

    private static object AccountFields(SessionUser user) => new
    {
        userId = user.Id,
        email = user.Email,
        displayName = (string?)null,
    };

    public static void LogEvent(
        ILogger logger,
        string action,
        string outcome,
        string ipHash,
        long startedAt,
        string? userId = null,
        int? status = null)
    {
        var elapsed = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var shortIp = ipHash.Length > 12 ? ipHash[..12] : ipHash;
        logger.LogInformation(
            "auth.{Action} outcome={Outcome} userId={UserId} ipHash={IpHash} status={Status} elapsedMs={ElapsedMs}",
            action,
            outcome,
            userId ?? string.Empty,
            shortIp,
            status?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            elapsed);
    }
}

public sealed record EmailCodeRequest(string? Email, string? Purpose);

public sealed record RegisterRequest(
    string? Email,
    string? Password,
    string? Code,
    string? DeviceLabel,
    string? Platform);

public sealed record LoginRequest(string? Email, string? Password, string? DeviceLabel, string? Platform);

public sealed record CodeLoginRequest(
    string? Email,
    string? Code,
    string? DeviceLabel,
    string? Platform);

public sealed record RefreshRequest(string? RefreshToken);

public sealed record LogoutRequest(string? RefreshToken, bool? AllDevices);

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(
    string? Email,
    string? Code,
    string? NewPassword,
    string? Password);

public sealed record AccountDeleteRequest(string? Password, string? Code);

public sealed record RevokeAllSessionsRequest(bool? KeepCurrent);
