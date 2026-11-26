namespace Mosaic.Mobile.Core.Projection;

/// <summary>Where the projection came from: the uploaded snapshot and its cursor.</summary>
public sealed record ProjectionBaseline(
    string SnapshotId,
    long ServerRevision,
    DateTimeOffset SnapshotCreatedAt,
    string? Cursor,
    DateTimeOffset RestoredAtUtc);

public sealed record ProjectionCounts(int Visible, int Open, int Archived);

public enum ProjectionOpenStatus
{
    Opened = 0,
    OwnerMismatch,
    SchemaMismatch,
    Corrupt,
}

public sealed record ProjectionOpenResult(ProjectionOpenStatus Status, TaskProjection? Projection, string? Detail)
{
    public bool IsOpened => Status == ProjectionOpenStatus.Opened && Projection is not null;

    public static ProjectionOpenResult Opened(TaskProjection projection) => new(ProjectionOpenStatus.Opened, projection, null);

    public static ProjectionOpenResult Failed(ProjectionOpenStatus status, string detail) => new(status, null, detail);
}
