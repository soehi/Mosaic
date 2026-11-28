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
    public const string AttachmentHashMismatch = "SYNC_ATTACHMENT_HASH_MISMATCH";
    public const string AttachmentsInvalid = "SYNC_ATTACHMENTS_INVALID";
}

public sealed record BackupItemDto(string Kind, string Key, string Payload, long Revision);

public sealed record BackupSnapshotDto(
    string SnapshotId,
    DateTimeOffset CreatedAtUtc,
    int ItemCount,
    string ContentSha256,
    long TotalBytes,
    long ServerRevision = 0,
    string? Cursor = null,
    // 服务器清单里 task 条目的数量；旧服务器不返回时为 -1，调用方应跳过空任务保护。
    int TaskCount = -1);

public sealed record BackupSnapshotContentDto(BackupSnapshotDto Snapshot, IReadOnlyList<BackupItemDto> Items);

public sealed record BackupUploadResult(
    string SnapshotId, int TaskCount, int SettingCount, long TotalBytes, long ServerRevision = 0, string? Cursor = null,
    int AttachmentCount = 0, int DeletedAttachmentCount = 0);

public sealed record RestoreSummary(int TaskCount, int SettingCount, int AttachmentCount = 0);

/// <summary>服务器端附件元数据（来自 /v1/attachments 清单）。</summary>
public sealed record RemoteAttachmentDto(Guid Id, Guid TaskId, string FileName, long SizeBytes, string Sha256, DateTimeOffset AddedAtUtc = default);

/// <summary>当前账号在服务器上的附件清单与配额。</summary>
public sealed record AttachmentManifestDto(
    IReadOnlyList<RemoteAttachmentDto> Items, long UsageBytes, long QuotaBytes, long MaxBytes);

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

    /// <summary>读取当前账号在服务器上的附件清单（GET /v1/attachments）。</summary>
    Task<OperationResult<AttachmentManifestDto>> ListAttachmentsAsync(Uri server, string accessToken, CancellationToken cancellationToken = default);

    /// <summary>上传单个附件内容（PUT /v1/attachments/{id}），<paramref name="sha256"/> 用于服务器端完整性校验。</summary>
    Task<OperationResult> UploadAttachmentAsync(Uri server, string accessToken, Guid taskId, Guid attachmentId, string fileName,
        Stream content, string sha256, CancellationToken cancellationToken = default);

    /// <summary>把附件内容流式写入 <paramref name="targetPath"/> 并校验 SHA-256；不符时删除落地文件并返回 <see cref="SyncErrorCodes.AttachmentHashMismatch"/>。</summary>
    Task<OperationResult> DownloadAttachmentAsync(Uri server, string accessToken, Guid attachmentId, string targetPath,
        string expectedSha256, CancellationToken cancellationToken = default);

    /// <summary>删除服务器上的附件；服务器返回 404 视为成功（幂等）。</summary>
    Task<OperationResult> DeleteAttachmentAsync(Uri server, string accessToken, Guid attachmentId, CancellationToken cancellationToken = default);
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
