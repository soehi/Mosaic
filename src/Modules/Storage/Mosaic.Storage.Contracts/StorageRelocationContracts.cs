using Mosaic.Contracts;

namespace Mosaic.Storage.Contracts;

/// <summary>Describes where the active database lives and how large it is on disk.</summary>
public sealed record StorageLocationInfo(string Directory, string DatabasePath, long SizeBytes);

/// <summary>Requested relocation of the database into an absolute target directory.</summary>
public sealed record StorageRelocationRequest(string TargetDirectory, bool KeepSource = true);

/// <summary>Validated, immutable description of a pending relocation.</summary>
public sealed record StorageRelocationPlan(
    string SourceDatabasePath,
    string TargetDirectory,
    string TargetDatabasePath,
    long SourceBytes,
    bool KeepSource);

/// <summary>Outcome of the last startup relocation, shown once on the maintenance page.</summary>
public sealed record StorageRelocationResult(
    bool Success,
    string Message,
    string? DatabasePath,
    DateTimeOffset CompletedAt);

public static class StorageRelocationFiles
{
    public const string RequestFileName = "storage-relocation.json";
    public const string ResultFileName = "storage-relocation-result.json";
}

/// <summary>
/// Plans and schedules relocation of the SQLite database. Execution happens on the next startup,
/// before the storage module opens its long-lived connection.
/// </summary>
public interface IStorageRelocation
{
    Task<OperationResult<StorageLocationInfo>> GetLocationAsync(CancellationToken cancellationToken = default);

    Task<OperationResult<StorageRelocationPlan>> PlanAsync(
        StorageRelocationRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> ScheduleAsync(
        StorageRelocationPlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>Returns and clears the outcome of the last startup relocation, if any.</summary>
    Task<StorageRelocationResult?> ReadResultAsync(CancellationToken cancellationToken = default);
}
