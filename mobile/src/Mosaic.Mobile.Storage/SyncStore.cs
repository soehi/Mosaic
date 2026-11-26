namespace Mosaic.Mobile.Storage;

public enum MobileTaskFilter
{
    /// <summary>All tasks which have not been archived.</summary>
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

public enum ProjectionFreshness
{
    Unverified,
    Bootstrapping,
    Syncing,
    Ready,
    Stale,
    NeedsRestore,
    Failed,
    SnapshotTooLarge,
}

public sealed record ProjectionBaseline
{
    public ProjectionBaseline(
        string snapshotId,
        long serverRevision,
        DateTimeOffset createdAtUtc,
        string cursor)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            throw new ArgumentException("A nonempty snapshot id is required.", nameof(snapshotId));
        }

        if (serverRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(serverRevision));
        }

        SnapshotId = snapshotId;
        ServerRevision = serverRevision;
        CreatedAtUtc = createdAtUtc;
        Cursor = ProjectionCursor.Validate(cursor);
    }

    public string SnapshotId { get; }
    public long ServerRevision { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string Cursor { get; }
}

/// <summary>Opaque cursor. It is never parsed or compared numerically.</summary>
public static class ProjectionCursor
{
    public static string Validate(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            throw new ArgumentException("A nonempty opaque projection cursor is required.", nameof(cursor));
        }

        return cursor;
    }
}

public sealed record SyncState(
    string? Cursor,
    string? BaselineSnapshotId,
    long? BaselineServerRevision,
    DateTimeOffset? BaselineCreatedAtUtc,
    long MaxServerRevision,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastCompleteSyncAt,
    DateTimeOffset? LastSuccessfulPageAt,
    ProjectionFreshness Freshness)
{
    public static SyncState Empty { get; } = new(
        null, null, null, null, 0, null, null, null, ProjectionFreshness.Unverified);
}

/// <summary>A fully validated page. Rows and cursor are committed atomically.</summary>
public sealed record SyncPage
{
    public SyncPage(
        IReadOnlyList<SyncedTask> tasks,
        IReadOnlyList<TaskTombstone> tombstones,
        string nextCursor,
        bool hasMore,
        DateTimeOffset syncedAt,
        string? expectedCursor)
    {
        Tasks = tasks;
        Tombstones = tombstones;
        NextCursor = ProjectionCursor.Validate(nextCursor);
        HasMore = hasMore;
        SyncedAt = syncedAt;
        ExpectedCursor = expectedCursor is null ? null : ProjectionCursor.Validate(expectedCursor);
    }

    public IReadOnlyList<SyncedTask> Tasks { get; }
    public IReadOnlyList<TaskTombstone> Tombstones { get; }
    public string NextCursor { get; }
    public bool HasMore { get; }
    public DateTimeOffset SyncedAt { get; }
    public string? ExpectedCursor { get; }
}

public interface ISyncStore
{
    ProjectionIdentity Identity { get; }

    Task<SyncState> GetStateAsync(CancellationToken cancellationToken = default);

    Task RecordAttemptAsync(ProjectionFreshness freshness, DateTimeOffset at, CancellationToken cancellationToken = default);

    Task ApplyPageAsync(SyncPage page, CancellationToken cancellationToken = default);

    Task CompleteSyncAsync(DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Full wipe after sign-out or account deletion.</summary>
    Task ClearAllAsync(CancellationToken cancellationToken = default);

    Task ReplaceProjectionAsync(
        IReadOnlyList<SyncedTask> tasks,
        ProjectionBaseline baseline,
        DateTimeOffset restoredAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncedTask>> ListTasksAsync(
        MobileTaskFilter filter,
        CancellationToken cancellationToken = default);

    Task<SyncedTask?> GetTaskAsync(Guid id, CancellationToken cancellationToken = default);

    Task<int> CountTasksAsync(CancellationToken cancellationToken = default);
}
