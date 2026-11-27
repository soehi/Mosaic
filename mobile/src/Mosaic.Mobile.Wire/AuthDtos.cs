namespace Mosaic.Mobile.Wire;

public sealed record UserDto
{
    public string? Id { get; init; }

    public string? Email { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? EmailVerifiedAt { get; init; }
}

public sealed record AccountDto
{
    public string? UserId { get; init; }

    public string? Email { get; init; }

    public string? DisplayName { get; init; }
}

/// <summary>The token pair returned by login, refresh and the device flow.</summary>
public sealed record SessionDto
{
    public string? AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    public DateTimeOffset? AccessTokenExpiresAt { get; init; }

    public DateTimeOffset? RefreshTokenExpiresAt { get; init; }

    public long ExpiresIn { get; init; }

    public UserDto? User { get; init; }

    public AccountDto? Account { get; init; }
}

public sealed record RefreshRequestDto(string RefreshToken);

public sealed record DeviceStartRequestDto
{
    public string? DeviceLabel { get; init; }

    public string? Platform { get; init; }

    public string? AppVersion { get; init; }
}

public sealed record DeviceStartDto
{
    public string? DeviceCode { get; init; }

    public string? UserCode { get; init; }

    public string? VerificationUri { get; init; }

    public string? VerificationUriComplete { get; init; }

    public long ExpiresIn { get; init; }

    public long Interval { get; init; }
}

public sealed record DevicePollRequestDto(string DeviceCode);

public sealed record DevicePollDto
{
    public string? Status { get; init; }

    public long Interval { get; init; }

    public SessionDto? Session { get; init; }
}

public static class DevicePollStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Expired = "expired";
}

public static class AuthErrorCodes
{
    public static bool IsSessionFailure(string? code) => code is not null && (
        code.StartsWith("auth.", StringComparison.Ordinal)
        || string.Equals(code, "auth.required", StringComparison.Ordinal));
}