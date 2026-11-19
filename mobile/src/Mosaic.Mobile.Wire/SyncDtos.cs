using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mosaic.Mobile.Wire;

public enum ChangeKind
{
    Unknown = 0,
    Task,
    Setting,
}

public enum ChangeOperation
{
    Unknown = 0,
    Upsert,
    Delete,
}

/// <summary>
/// One incremental change.
///
/// Server 0.1.1 emits <c>cursor</c>, <c>serverRevision</c>, <c>deleted</c>
/// (bool) and <c>createdAt</c>; that shape is authoritative. <c>operation</c>
/// and <c>updatedAtUtc</c> are still read from older builds and from the
/// pre-0.1.1 frozen samples, but only when the canonical fields are absent.
/// </summary>
public sealed record ChangeDto
{
    public long ServerRevision { get; init; }

    public string? Kind { get; init; }

    public string? Key { get; init; }

    /// <summary>Legacy field (<c>upsert</c>|<c>delete</c>); canonical is <see cref="Deleted"/>.</summary>
    public string? Operation { get; init; }

    /// <summary>Canonical tombstone flag.</summary>
    public bool? Deleted { get; init; }

    /// <summary>Task payload or <c>{"value":...}</c>; omitted for tombstones.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>Legacy timestamp; canonical is <see cref="CreatedAt"/>.</summary>
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    /// <summary>Canonical change timestamp.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    public string? DeviceId { get; init; }

    /// <summary>Opaque per-row cursor returned by the server; never parsed.</summary>
    public string? Cursor { get; init; }

    [JsonIgnore]
    public ChangeKind ResolvedKind => Kind?.Trim().ToLowerInvariant() switch
    {
        "task" => ChangeKind.Task,
        "setting" => ChangeKind.Setting,
        _ => ChangeKind.Unknown,
    };

    [JsonIgnore]
    public ChangeOperation ResolvedOperation
    {
        get
        {
            // Canonical field wins when present, even if a legacy field disagrees.
            if (Deleted is { } deleted)
            {
                if (deleted)
                {
                    return ChangeOperation.Delete;
                }

                return Payload is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined }
                    ? ChangeOperation.Upsert
                    : ChangeOperation.Unknown;
            }

            if (string.Equals(Operation, "delete", StringComparison.OrdinalIgnoreCase))
            {
                return ChangeOperation.Delete;
            }

            if (string.Equals(Operation, "upsert", StringComparison.OrdinalIgnoreCase))
            {
                return ChangeOperation.Upsert;
            }

            // An explicit but unrecognized operation is never guessed: a newer
            // server may mean something the old app must not apply.
            if (!string.IsNullOrEmpty(Operation))
            {
                return ChangeOperation.Unknown;
            }

            return Payload is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined }
                ? ChangeOperation.Upsert
                : ChangeOperation.Unknown;
        }
    }

    [JsonIgnore]
    public DateTimeOffset? EffectiveTimestamp => UpdatedAtUtc ?? CreatedAt;
}

public sealed record SyncChangesResponseDto
{
    public List<ChangeDto> Changes { get; init; } = [];

    public string? NextCursor { get; init; }

    public bool HasMore { get; init; }
}

// --- Write shapes (server 0.1.1: one change per POST, no batch) ---

/// <summary>Body of <c>POST /v1/sync/changes</c>; one change per request.</summary>
public sealed record ChangePushRequestDto
{
    public string? DeviceId { get; init; }

    public string? Kind { get; init; }

    public string? Key { get; init; }

    /// <summary>Task payload or <c>{"value":...}</c>; omitted for tombstones.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Payload { get; init; }

    public bool Deleted { get; init; }

    /// <summary>Server revision the write was based on; 0 for a new item.</summary>
    public long BaseServerRevision { get; init; }

    public long ClientRevision { get; init; }

    public string? IdempotencyKey { get; init; }
}

/// <summary><c>{change,replayed}</c>; <c>replayed</c> means the idempotency key was seen before.</summary>
public sealed record SyncPushResponseDto
{
    public ChangeDto? Change { get; init; }

    public bool Replayed { get; init; }
}

/// <summary>Conflict payload <c>details.current</c>: the server's winning record.</summary>
public sealed record SyncConflictCurrentDto
{
    public string? Kind { get; init; }

    public string? Key { get; init; }

    public long ServerRevision { get; init; }

    public bool Deleted { get; init; }

    public JsonElement? Payload { get; init; }
}

public sealed record SyncConflictDetailsDto
{
    public SyncConflictCurrentDto? Current { get; init; }
}
