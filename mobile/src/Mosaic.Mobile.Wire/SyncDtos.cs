using System.Text.Json;

namespace Mosaic.Mobile.Wire;

/// <summary>
/// One single-task command. The mobile client only ever pushes kind
/// <c>task</c> entries, never tombstones: archiving is expressed as a payload
/// change (<c>archivedAtUtc</c>), not as <c>deleted=true</c>.
/// </summary>
public sealed record ChangePushRequestDto
{
    public string DeviceId { get; init; } = string.Empty;

    public string Kind { get; init; } = SyncKinds.Task;

    public string Key { get; init; } = string.Empty;

    public JsonElement? Payload { get; init; }

    public bool Deleted { get; init; }

    public long BaseServerRevision { get; init; }

    public long ClientRevision { get; init; }

    public string IdempotencyKey { get; init; } = string.Empty;
}

public sealed record SyncChangeDto
{
    public string? Cursor { get; init; }

    public long ServerRevision { get; init; }

    public string? Kind { get; init; }

    public string? Key { get; init; }

    public bool Deleted { get; init; }

    public JsonElement? Payload { get; init; }

    public string? DeviceId { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }
}

public sealed record ChangePushResponseDto
{
    public SyncChangeDto? Change { get; init; }

    public bool Replayed { get; init; }
}

public sealed record SyncChangePageDto
{
    public List<SyncChangeDto>? Changes { get; init; }

    public string? NextCursor { get; init; }

    public bool HasMore { get; init; }
}

public sealed record SyncConflictDetailsDto
{
    public SyncConflictCurrentDto? Current { get; init; }
}

public sealed record SyncConflictCurrentDto
{
    public string? Kind { get; init; }

    public string? Key { get; init; }

    public long ServerRevision { get; init; }

    public bool Deleted { get; init; }

    public JsonElement? Payload { get; init; }
}

public static class SyncKinds
{
    public const string Task = "task";

    public const string Setting = "setting";
}