using System.Text.Json;

namespace Mosaic.Mobile.Wire;

/// <summary>
/// Snapshot metadata. The shared document mentions <c>serverRevision</c> and
/// <c>cursor</c> on the latest-backup response; the current server does not
/// send them, so both are optional here.
/// </summary>
public sealed record BackupManifestDto
{
    public string? SnapshotId { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public string? BaseSnapshotId { get; init; }

    public string? ContentSha256 { get; init; }

    public long SizeBytes { get; init; }

    public int ItemCount { get; init; }

    public List<BackupManifestItemDto> Items { get; init; } = [];

    public long? ServerRevision { get; init; }

    public string? Cursor { get; init; }
}

public sealed record BackupManifestItemDto(string? Kind, string? Key, long? Revision);

public sealed record BackupSnapshotDto
{
    public string? SnapshotId { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public string? BaseSnapshotId { get; init; }

    public string? ContentSha256 { get; init; }

    public long SizeBytes { get; init; }

    public int ItemCount { get; init; }

    public List<BackupItemDto> Items { get; init; } = [];

    public long? ServerRevision { get; init; }

    public string? Cursor { get; init; }
}

public sealed record BackupItemDto
{
    public string? Kind { get; init; }

    public string? Key { get; init; }

    public long? Revision { get; init; }

    /// <summary>Task shape or <c>{"value":...}</c> for settings; opaque to the wire layer.</summary>
    public JsonElement? Payload { get; init; }
}
