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
/// The shared protocol freezes <c>operation</c> (<c>upsert|delete</c>) and
/// <c>updatedAtUtc</c>. The current server build emits <c>deleted</c> (bool),
/// <c>createdAt</c> and a per-row <c>cursor</c> instead. Both shapes are read
/// here; the frozen fields stay authoritative when present. The drift is
/// reported in docs/mobile-implementation-status.md — the mobile side does not
/// change either protocol definition.
/// </summary>
public sealed record ChangeDto
{
    public long ServerRevision { get; init; }

    public string? Kind { get; init; }

    public string? Key { get; init; }

    /// <summary>Frozen field: <c>upsert</c> or <c>delete</c>.</summary>
    public string? Operation { get; init; }

    /// <summary>Server alias for <see cref="Operation"/>: true means tombstone.</summary>
    public bool? Deleted { get; init; }

    /// <summary>Task payload or <c>{"value":...}</c>; null for tombstones.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>Frozen field.</summary>
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    /// <summary>Server alias for <see cref="UpdatedAtUtc"/>.</summary>
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

            if (Deleted == true)
            {
                return ChangeOperation.Delete;
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

// --- Phase 2 write shapes (kept so the local outbox schema matches the wire) ---

public sealed record ChangePushDto
{
    public string? Kind { get; init; }

    public string? Key { get; init; }

    public string? Operation { get; init; }

    public JsonElement? Payload { get; init; }

    public long? BaseServerRevision { get; init; }

    public long? ClientRevision { get; init; }

    public string? IdempotencyKey { get; init; }
}

public sealed record SyncPushRequestDto(string? DeviceId, List<ChangePushDto> Changes);

public sealed record SyncPushResponseDto
{
    public ChangeDto? Change { get; init; }

    public bool Replayed { get; init; }
}
