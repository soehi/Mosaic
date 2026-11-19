namespace Mosaic.Mobile.Wire;

/// <summary>Public server policy fetched at startup / when a server URL is chosen.</summary>
public sealed record AuthPolicyDto
{
    public string? ServerName { get; init; }

    public bool RegistrationOpen { get; init; } = true;

    public bool RequireEmailVerification { get; init; } = true;

    public bool EmailAvailable { get; init; }

    public int CodeLength { get; init; } = 6;

    public int CodeTtlSeconds { get; init; } = 600;

    public int ResendAfterSeconds { get; init; } = 60;

    public int PasswordMinLength { get; init; } = 10;

    public int PasswordMaxLength { get; init; } = 256;
}

public sealed record EmailCodeRequestDto(string? Email, string? Purpose);

/// <summary>Unified 202 response; never reveals whether the address is registered.</summary>
public sealed record EmailCodeAcceptedDto
{
    public string? Status { get; init; }

    public int? ResendAfterSeconds { get; init; }
}

public sealed record RegisterRequestDto
{
    public string? Email { get; init; }

    public string? Password { get; init; }

    public string? Code { get; init; }

    public string? DeviceLabel { get; init; }
}

public sealed record LoginRequestDto
{
    public string? Email { get; init; }

    public string? Password { get; init; }

    public string? DeviceLabel { get; init; }
}

public sealed record CodeLoginRequestDto
{
    public string? Email { get; init; }

    public string? Code { get; init; }

    public string? DeviceLabel { get; init; }
}

public sealed record RefreshRequestDto(string? RefreshToken);

public sealed record LogoutRequestDto(string? RefreshToken, bool? AllDevices);

public sealed record ForgotPasswordRequestDto(string? Email);

public sealed record ResetPasswordRequestDto
{
    public string? Email { get; init; }

    public string? Code { get; init; }

    public string? NewPassword { get; init; }
}

public sealed record SimpleStatusDto(string? Status);

/// <summary>Account-delete payload: exactly one of password / fresh email code.</summary>
public sealed record DeleteAccountRequestDto
{
    public string? Password { get; init; }

    public string? Code { get; init; }
}

public static class EmailCodePurpose
{
    public const string Register = "register";

    public const string Login = "login";

    public const string Reset = "reset";

    public const string DeleteAccount = "delete-account";
}
