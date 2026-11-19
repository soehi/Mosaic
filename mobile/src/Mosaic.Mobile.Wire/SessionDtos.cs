using System.Text.Json;

namespace Mosaic.Mobile.Wire;

public sealed record UserDto
{
    public string? Id { get; init; }

    public string? Email { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? EmailVerifiedAt { get; init; }
}

public sealed record SessionDto
{
    public string? AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    public DateTimeOffset? AccessTokenExpiresAt { get; init; }

    public DateTimeOffset? RefreshTokenExpiresAt { get; init; }

    public UserDto? User { get; init; }
}

public sealed record MeResponseDto(UserDto? User);

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
    /// <summary>
    /// Accepts either a bare array or a <c>{"sessions":[...]}</c> envelope. The
    /// shared document freezes the fields but not the envelope, and the server
    /// endpoint is not implemented yet; accepting both avoids a needless break.
    /// </summary>
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
