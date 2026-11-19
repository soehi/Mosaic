using Mosaic.Contracts;

namespace Mosaic.Sync.Contracts;

public static class SyncItemKinds
{
    public const string Task = "task";
    public const string Setting = "setting";
}

public static class SyncErrorCodes
{
    public const string NotSignedIn = "SYNC_NOT_SIGNED_IN";
    public const string SettingsInvalid = "SYNC_SETTINGS_INVALID";
    public const string SnapshotInvalid = "SYNC_SNAPSHOT_INVALID";
    public const string TasksImportFailed = "SYNC_TASKS_IMPORT_FAILED";
    public const string Disabled = "SYNC_DISABLED";
    public const string ServerUntrusted = "SYNC_SERVER_UNTRUSTED";
    public const string NetworkUnavailable = "SYNC_NETWORK_UNAVAILABLE";
}

public sealed record BackupItemDto(string Kind, string Key, string Payload, long Revision);

public sealed record BackupSnapshotDto(
    string SnapshotId,
    DateTimeOffset CreatedAtUtc,
    int ItemCount,
    string ContentSha256,
    long TotalBytes,
    long ServerRevision = 0,
    string? Cursor = null);

public sealed record BackupSnapshotContentDto(BackupSnapshotDto Snapshot, IReadOnlyList<BackupItemDto> Items);

public sealed record BackupUploadResult(string SnapshotId, int TaskCount, int SettingCount, long TotalBytes, long ServerRevision = 0, string? Cursor = null);

public sealed record RestoreSummary(int TaskCount, int SettingCount);

public sealed record SyncState(
    bool IsSignedIn,
    bool IsBusy,
    BackupSnapshotDto? LatestSnapshot,
    RestoreSummary? LastRestore,
    string? LastMessageKey);

public interface ISyncTransport
{
    Task<OperationResult<BackupSnapshotDto>> UploadAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        IReadOnlyList<BackupItemDto> items,
        CancellationToken cancellationToken = default);
    Task<OperationResult<BackupSnapshotDto?>> GetLatestAsync(Uri server, string accessToken, CancellationToken cancellationToken = default);
    Task<OperationResult<BackupSnapshotContentDto>> DownloadAsync(Uri server, string accessToken, string snapshotId, CancellationToken cancellationToken = default);
}

public interface ISyncApi
{
    SyncState State { get; }
    event EventHandler? StateChanged;

    Task<OperationResult<BackupSnapshotDto?>> RefreshLatestAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<BackupUploadResult>> UploadAsync(bool includeSettings, CancellationToken cancellationToken = default);
    // Replaces all local tasks with the downloaded backup. Callers must confirm with the user first.
    Task<OperationResult<RestoreSummary>> DownloadAndReplaceAsync(CancellationToken cancellationToken = default);
}
