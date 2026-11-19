namespace Mosaic.Mobile.Storage;

public enum MobileTaskFilter
{
    All = 0,
    Open,
    Completed,
    Archived,
}

public sealed record SyncedTask
{
    public Guid Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public bool IsCompleted { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public long Revision { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public DateTimeOffset? ArchivedAtUtc { get; init; }

    public DateTimeOffset? RestoredAtUtc { get; init; }

    public long ServerRevision { get; init; }

    public string? DeviceId { get; init; }
}

public sealed record TaskTombstone(Guid Id, long ServerRevision, DateTimeOffset? DeletedAtUtc);

public sealed record StoredSetting(
    string Key,
    string ValueJson,
    long ServerRevision,
    DateTimeOffset? UpdatedAtUtc);

public sealed record SyncState(string? Cursor, DateTimeOffset? LastSyncedAt, long MaxServerRevision)
{
    public static SyncState Empty { get; } = new(null, null, 0);
}

/// <summary>A fully projected page: the store never sees wire DTOs.</summary>
public sealed record SyncPage(
    IReadOnlyList<SyncedTask> Tasks,
    IReadOnlyList<TaskTombstone> Tombstones,
    IReadOnlyList<StoredSetting> Settings,
    string? NextCursor,
    DateTimeOffset SyncedAt);

/// <summary>
/// Storage contract consumed by the sync engine and the task list. Implemented
/// by <see cref="SqliteSyncStore"/>; tests use the same type against a temp file.
/// </summary>
public interface ISyncStore
{
    Task<SyncState> GetStateAsync(CancellationToken cancellationToken = default);

    Task ApplyPageAsync(SyncPage page, CancellationToken cancellationToken = default);

    /// <summary>Drops tasks, settings and tombstones but keeps the schema and outbox.</summary>
    Task ResetProjectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Full wipe after sign-out or account deletion.</summary>
    Task ClearAllAsync(CancellationToken cancellationToken = default);

    Task ReplaceProjectionAsync(
        IReadOnlyList<SyncedTask> tasks,
        IReadOnlyList<StoredSetting> settings,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncedTask>> ListTasksAsync(
        MobileTaskFilter filter,
        CancellationToken cancellationToken = default);

    Task<SyncedTask?> GetTaskAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredSetting>> ListSettingsAsync(CancellationToken cancellationToken = default);

    Task<int> CountTasksAsync(CancellationToken cancellationToken = default);
}
