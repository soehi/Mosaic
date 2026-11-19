using System.Text.Json;

namespace Mosaic.Mobile.Wire;

public sealed record UserDto
{
    public string? Id { get; init; }

    public string? Email { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? EmailVerifiedAt { get; init; }
}

/// <summary>Account view carried alongside <c>user</c> by server 0.1.1.</summary>
public sealed record AccountDto
{
    public string? UserId { get; init; }

    public string? Email { get; init; }

    public string? DisplayName { get; init; }
}

/// <summary>
/// Session response is a superset: <c>user</c> and/or <c>account</c>, and
/// <c>expiresIn</c> alongside the absolute expiry. <see cref="Normalize"/>
/// turns any accepted shape into the one internal shape the client stores.
/// </summary>
public sealed record SessionDto
{
    public string? AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    public DateTimeOffset? AccessTokenExpiresAt { get; init; }

    public DateTimeOffset? RefreshTokenExpiresAt { get; init; }

    public long? ExpiresIn { get; init; }

    public UserDto? User { get; init; }

    public AccountDto? Account { get; init; }

    public UserDto? EffectiveUser =>
        User ?? (Account is { } account && (account.UserId is not null || account.Email is not null)
            ? new UserDto { Id = account.UserId, Email = account.Email }
            : null);

    public DateTimeOffset? EffectiveAccessTokenExpiresAt(DateTimeOffset now) =>
        AccessTokenExpiresAt
        ?? (ExpiresIn is > 0 ? now.AddSeconds(ExpiresIn.Value) : null);

    public SessionDto Normalize(DateTimeOffset now) => this with
    {
        AccessTokenExpiresAt = EffectiveAccessTokenExpiresAt(now),
        User = EffectiveUser,
    };
}

/// <summary>
/// <c>/v1/auth/me</c> returns the account fields at the root with a nested
/// <c>user</c>; either shape resolves to the same identity.
/// </summary>
public sealed record MeResponseDto
{
    public UserDto? User { get; init; }

    public string? UserId { get; init; }

    public string? Email { get; init; }

    public AccountDto? Account { get; init; }

    public UserDto? EffectiveUser =>
        User
        ?? (Account is { } account && (account.UserId is not null || account.Email is not null)
            ? new UserDto { Id = account.UserId, Email = account.Email }
            : null)
        ?? (UserId is not null || Email is not null
            ? new UserDto { Id = UserId, Email = Email }
            : null);
}

/// <summary>A session/device row. Never contains tokens.</summary>
public sealed record SessionInfoDto
{
    public string? Id { get; init; }

    public string? DeviceLabel { get; init; }

    public string? Platform { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public bool Current { get; init; }
}

public static class SessionListParser
{
    /// <summary>Accepts a bare array or a <c>{"sessions":[...]}</c> envelope.</summary>
    public static IReadOnlyList<SessionInfoDto> Parse(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return Deserialize(root);
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("sessions", out var sessions)
            && sessions.ValueKind == JsonValueKind.Array)
        {
            return Deserialize(sessions);
        }

        return [];
    }

    private static IReadOnlyList<SessionInfoDto> Deserialize(JsonElement array) =>
        array.Deserialize<List<SessionInfoDto>>(WireJson.Options) ?? [];
}
