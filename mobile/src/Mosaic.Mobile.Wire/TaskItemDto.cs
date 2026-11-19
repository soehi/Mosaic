namespace Mosaic.Mobile.Wire;

/// <summary>
/// Wire shape of a task inside a sync change payload. Mirrors the desktop
/// <c>TaskItemDto</c> exactly; nullable members tolerate older publishers that
/// predate completed/archived/restored timestamps.
/// </summary>
public sealed record TaskItemDto
{
    public Guid? Id { get; init; }

    public string? Title { get; init; }

    public string? Notes { get; init; }

    public bool IsCompleted { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public long Revision { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public DateTimeOffset? ArchivedAtUtc { get; init; }

    public DateTimeOffset? RestoredAtUtc { get; init; }
}

/// <summary>Setting payload is always <c>{"value": &lt;scalar&gt;}</c>.</summary>
public sealed record SettingPayloadDto
{
    public System.Text.Json.JsonElement Value { get; init; }
}
