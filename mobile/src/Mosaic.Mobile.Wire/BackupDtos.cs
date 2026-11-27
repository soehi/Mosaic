using System.Text.Json;

namespace Mosaic.Mobile.Wire;

/// <summary>
/// Latest-backup manifest. <see cref="Cursor"/> is the sync cursor at the time
/// the snapshot was uploaded: the projection baseline starts there and the
/// incremental pull continues from it. <see cref="ServerRevision"/> is source
/// metadata only; it must never be stamped onto individual tasks.
/// </summary>
public sealed record BackupManifestDto
{
    public string? SnapshotId { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public string? BaseSnapshotId { get; init; }

    public string? ContentSha256 { get; init; }

    public long SizeBytes { get; init; }

    public int ItemCount { get; init; }

    public long ServerRevision { get; init; }

    public string? Cursor { get; init; }
}

public sealed record BackupSnapshotDto
{
    public string? SnapshotId { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public string? BaseSnapshotId { get; init; }

    public string? ContentSha256 { get; init; }

    public long SizeBytes { get; init; }

    public int ItemCount { get; init; }

    public long ServerRevision { get; init; }

    public string? Cursor { get; init; }

    public List<BackupItemDto>? Items { get; init; }
}

public sealed record BackupItemDto
{
    public string? Kind { get; init; }

    public string? Key { get; init; }

    public long Revision { get; init; }

    /// <summary>Task shape, or <c>{"value":...}</c> for settings. Opaque here.</summary>
    public JsonElement? Payload { get; init; }
}