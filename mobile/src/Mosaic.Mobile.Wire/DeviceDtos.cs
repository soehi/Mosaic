namespace Mosaic.Mobile.Wire;

public sealed record DeviceStartRequestDto
{
    public string? DeviceLabel { get; init; }

    public string? Platform { get; init; }

    public string? AppVersion { get; init; }
}

public sealed record DeviceStartResponseDto
{
    public string? DeviceCode { get; init; }

    public string? UserCode { get; init; }

    public string? VerificationUri { get; init; }

    public string? VerificationUriComplete { get; init; }

    public long ExpiresIn { get; init; } = 600;

    public int Interval { get; init; } = 2;
}

public sealed record DevicePollRequestDto(string? DeviceCode);

public sealed record DevicePollResponseDto
{
    public string? Status { get; init; }

    /// <summary>Advertised cadence; present on every poll and on <c>slow_down</c>.</summary>
    public long? Interval { get; init; }

    /// <summary>Session is at the response root when approved.</summary>
    public SessionDto? Session { get; init; }
}

public sealed record DeviceCodeRequestDto(string? UserCode);

/// <summary>Descriptive information about a pending authorization (approval page).</summary>
public sealed record DeviceInfoDto
{
    public string? DeviceLabel { get; init; }

    public string? Platform { get; init; }

    public string? AppVersion { get; init; }

    public string? State { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? ApprovedAt { get; init; }
}

public static class DeviceFlowStatus
{
    public const string Pending = "pending";

    public const string Approved = "approved";

    public const string Denied = "denied";

    public const string Expired = "expired";

    public const string Consumed = "consumed";

    public const string SlowDown = "slow_down";
}

public enum DeviceFlowState
{
    Unknown = 0,
    Pending,
    Approved,
    Denied,
    Expired,
    Consumed,
}

public static class DeviceFlowStateParser
{
    public static DeviceFlowState Parse(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        DeviceFlowStatus.Pending => DeviceFlowState.Pending,
        DeviceFlowStatus.Approved => DeviceFlowState.Approved,
        DeviceFlowStatus.Denied => DeviceFlowState.Denied,
        DeviceFlowStatus.Expired => DeviceFlowState.Expired,
        DeviceFlowStatus.Consumed => DeviceFlowState.Consumed,
        _ => DeviceFlowState.Unknown,
    };
}
