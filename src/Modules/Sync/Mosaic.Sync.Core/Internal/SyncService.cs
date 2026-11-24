using System.Globalization;
using System.Text.Json;
using Mosaic.Auth.Contracts;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Sync.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Sync.Core.Internal;

// Backups carry tasks and a fixed whitelist of portable user settings. Host configuration, storage
// paths, window placement, presence/startup flags, the settings journal and every credential stay
// on this machine by construction: they are never named by this whitelist.
internal sealed class SyncService : ISyncApi
{
    // Server 0.1.1 rejects any setting key outside this list with 400 backup.invalid, so the
    // portable set must match docs/auth-sync-server-plan.md section 4.3 exactly — no prefixes,
    // no extras like tasks.revisionStart or tasks.floating.allowNotesEditing.
    private static readonly HashSet<string> PortableIds = new(StringComparer.Ordinal)
    {
        "ui.language",
        "appearance.theme",
        "tasks.sortOrder",
        "tasks.defaultFilter",
        "tasks.archive.autoEnabled",
        "tasks.archive.delayDays",
        "tasks.title.maxCharacters",
        "tasks.notes.maxCharacters",
        "tasks.title.required",
        "tasks.defaultCompleted",
        "tasks.confirmDelete",
        "tasks.confirmUnsaved",
        "tasks.showCharacterCount",
        "tasks.autoSwitchToAllOnHidden",
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAuthApi _auth;
    private readonly ISyncTransport _transport;
    private readonly ITaskTransferApi _tasks;
    private readonly ISettingsApi _settings;
    private readonly SyncOptions _options;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _stateLock = new();
    private BackupSnapshotDto? _latest;
    private RestoreSummary? _lastRestore;
    private bool _busy;
    private string? _messageCode;

    internal SyncService(IAuthApi auth, ISyncTransport transport, ITaskTransferApi tasks, ISettingsApi settings, SyncOptions options)
    {
        _auth = auth;
        _transport = transport;
        _tasks = tasks;
        _settings = settings;
        _options = options;
    }

    internal void RaiseForAuthChange()
    {
        // A previous account's snapshot and restore summary must not survive sign-out or account
        // deletion: they are rendered as "最近备份/上次恢复" on the next sign-in page.
        if (_auth.State.Status != AuthStatus.SignedIn)
        {
            lock (_stateLock)
            {
                _latest = null;
                _lastRestore = null;
                _messageCode = null;
            }
        }
        Raise();
    }

    public event EventHandler? StateChanged;

    public SyncState State
    {
        get
        {
            lock (_stateLock)
            {
                var signedIn = _auth.State.Status == AuthStatus.SignedIn;
                return new(
                    signedIn,
                    _busy,
                    signedIn ? _latest : null,
                    signedIn ? _lastRestore : null,
                    signedIn ? _messageCode : null);
            }
        }
    }

    public Task<OperationResult<BackupSnapshotDto?>> RefreshLatestAsync(CancellationToken cancellationToken = default) =>
        Guarded(async () =>
        {
            if (!_options.Enabled) return Fail<BackupSnapshotDto?>(SyncErrorCodes.Disabled, "账号备份已在设置中关闭。");
            var context = await ContextAsync(cancellationToken).ConfigureAwait(false);
            if (!context.IsSuccess) return OperationResult<BackupSnapshotDto?>.Failure(context.Error!);
            var latest = await _transport.GetLatestAsync(context.Value.Server, context.Value.Token, cancellationToken).ConfigureAwait(false);
            if (!latest.IsSuccess) return Fail<BackupSnapshotDto?>(latest.Error!.Code, latest.Error.Message);
            lock (_stateLock) _latest = latest.Value;
            Raise();
            return OperationResult<BackupSnapshotDto?>.Success(latest.Value);
        }, cancellationToken);

    public Task<OperationResult<BackupUploadResult>> UploadAsync(bool includeSettings, CancellationToken cancellationToken = default) =>
        Guarded(async () =>
        {
            if (!_options.Enabled) return Fail<BackupUploadResult>(SyncErrorCodes.Disabled, "账号备份已在设置中关闭。");
            var context = await ContextAsync(cancellationToken).ConfigureAwait(false);
            if (!context.IsSuccess) return OperationResult<BackupUploadResult>.Failure(context.Error!);
            var exported = await _tasks.ExportAllAsync(cancellationToken).ConfigureAwait(false);
            if (!exported.IsSuccess) return OperationResult<BackupUploadResult>.Failure(exported.Error!);
            var items = new List<BackupItemDto>(exported.Value.Count + 32);
            foreach (var task in exported.Value)
                items.Add(new(SyncItemKinds.Task, task.Id.ToString("D"), JsonSerializer.Serialize(task, JsonOptions), task.Revision));
            var settingsCount = 0;
            if (includeSettings && _options.IncludeSettings)
            {
                var portable = await PortableSettingsAsync(cancellationToken).ConfigureAwait(false);
                if (!portable.IsSuccess) return OperationResult<BackupUploadResult>.Failure(portable.Error!);
                items.AddRange(portable.Value);
                settingsCount = portable.Value.Count;
            }
            var uploaded = await _transport.UploadAsync(context.Value.Server, context.Value.Token, _auth.DeviceId, items, cancellationToken).ConfigureAwait(false);
            if (!uploaded.IsSuccess) return Fail<BackupUploadResult>(uploaded.Error!.Code, uploaded.Error.Message);
            lock (_stateLock)
            {
                _latest = uploaded.Value;
                _messageCode = null;
            }
            // 快照已落库：先把"最近备份"更新出去，即使随后附件失败，用户看到的仍是这份快照。
            Raise();
            var attachmentCount = 0;
            var deletedAttachmentCount = 0;
            if (_options.IncludeAttachments)
            {
                var mirrored = await UploadAttachmentsAsync(context.Value.Server, context.Value.Token, cancellationToken).ConfigureAwait(false);
                if (!mirrored.IsSuccess) return OperationResult<BackupUploadResult>.Failure(mirrored.Error!);
                attachmentCount = mirrored.Value.Ensured;
                deletedAttachmentCount = mirrored.Value.Deleted;
            }
            return OperationResult<BackupUploadResult>.Success(new(uploaded.Value.SnapshotId, exported.Value.Count, settingsCount, uploaded.Value.TotalBytes, uploaded.Value.ServerRevision, uploaded.Value.Cursor, attachmentCount, deletedAttachmentCount));
        }, cancellationToken);

    public Task<OperationResult<RestoreSummary>> DownloadAndReplaceAsync(CancellationToken cancellationToken = default) =>
        Guarded(async () =>
        {
            if (!_options.Enabled) return Fail<RestoreSummary>(SyncErrorCodes.Disabled, "账号备份已在设置中关闭。");
            var context = await ContextAsync(cancellationToken).ConfigureAwait(false);
            if (!context.IsSuccess) return OperationResult<RestoreSummary>.Failure(context.Error!);
            var latest = await _transport.GetLatestAsync(context.Value.Server, context.Value.Token, cancellationToken).ConfigureAwait(false);
            if (!latest.IsSuccess) return Fail<RestoreSummary>(latest.Error!.Code, latest.Error.Message);
            if (latest.Value is not { } snapshot)
                return Fail<RestoreSummary>(SyncErrorCodes.SnapshotInvalid, "服务器上还没有可恢复的备份。");
            var content = await _transport.DownloadAsync(context.Value.Server, context.Value.Token, snapshot.SnapshotId, cancellationToken).ConfigureAwait(false);
            if (!content.IsSuccess) return Fail<RestoreSummary>(content.Error!.Code, content.Error.Message);
            var prepared = await PrepareRestoreAsync(content.Value.Items, cancellationToken).ConfigureAwait(false);
            if (!prepared.IsSuccess) return OperationResult<RestoreSummary>.Failure(prepared.Error!);

            // 附件清单在动本地数据之前取：拿不到清单就整体放弃，避免任务被替换后才发现附件无法恢复。
            var remoteAttachments = new List<RemoteAttachmentDto>();
            if (_options.IncludeAttachments)
            {
                var manifest = await _transport.ListAttachmentsAsync(context.Value.Server, context.Value.Token, cancellationToken).ConfigureAwait(false);
                if (!manifest.IsSuccess) return Fail<RestoreSummary>(manifest.Error!.Code, manifest.Error.Message);
                var taskIds = prepared.Value.Tasks.Select(task => task.Id).ToHashSet();
                remoteAttachments.AddRange(manifest.Value.Items.Where(item => taskIds.Contains(item.TaskId)));
                // ReplaceAllAsync 在一个事务里清空全部 tasks 行，附件行会被外键级联清空。所以在它之前
                // 把"服务端没有的附件"（行 + 文件）删干净，本地只剩即将重新下载的那批，磁盘上不留旧副本。
                var keep = remoteAttachments.Select(item => item.Id).ToHashSet();
                var pruned = await _tasks.RemoveAttachmentsNotInAsync(keep, cancellationToken).ConfigureAwait(false);
                if (!pruned.IsSuccess) return Fail<RestoreSummary>(pruned.Error!.Code, pruned.Error.Message);
            }

            var replaced = await _tasks.ReplaceAllAsync(prepared.Value.Tasks, cancellationToken).ConfigureAwait(false);
            if (!replaced.IsSuccess)
                return Fail<RestoreSummary>(SyncErrorCodes.TasksImportFailed, "本地任务未能被备份覆盖：" + replaced.Error!.Message);
            var settingCount = 0;
            if (prepared.Value.Changes.Count > 0)
            {
                var applied = await ApplySettingsAsync(prepared.Value.Changes, cancellationToken).ConfigureAwait(false);
                if (!applied.IsSuccess) return OperationResult<RestoreSummary>.Failure(applied.Error!);
                settingCount = prepared.Value.Changes.Count;
            }

            var attachmentCount = 0;
            var attachmentErrors = new List<OperationError>();
            if (_options.IncludeAttachments && remoteAttachments.Count > 0)
            {
                var tempRoot = Path.Combine(Path.GetTempPath(), "mosaic-sync");
                Directory.CreateDirectory(tempRoot);
                foreach (var item in remoteAttachments)
                {
                    if (cancellationToken.IsCancellationRequested) return Fail<RestoreSummary>(ErrorCodes.Canceled, "操作已取消。");
                    var temp = Path.Combine(tempRoot, item.Id.ToString("D") + ".part");
                    try
                    {
                        var downloaded = await _transport.DownloadAttachmentAsync(context.Value.Server, context.Value.Token, item.Id, temp, item.Sha256, cancellationToken).ConfigureAwait(false);
                        if (!downloaded.IsSuccess)
                        {
                            attachmentErrors.Add(TagAttachmentError(item.FileName, downloaded.Error!));
                            continue;
                        }
                        var imported = await _tasks.ImportAttachmentAsync(new(item.Id, item.TaskId, item.FileName, item.SizeBytes, item.AddedAtUtc), temp, cancellationToken).ConfigureAwait(false);
                        if (!imported.IsSuccess) attachmentErrors.Add(TagAttachmentError(item.FileName, imported.Error!));
                        else attachmentCount++;
                    }
                    finally
                    {
                        TryDelete(temp);
                    }
                }
                // 只清 "Guid 目录 / Guid 文件名" 布局里的无行文件；用户手动放进目录的文件永不触碰。
                await _tasks.SweepOrphanAttachmentsAsync(cancellationToken).ConfigureAwait(false);
            }

            var summary = new RestoreSummary(prepared.Value.Tasks.Count, settingCount, attachmentCount);
            lock (_stateLock)
            {
                _latest = snapshot;
                _lastRestore = summary;
                _messageCode = null;
            }
            Raise();
            if (attachmentErrors.Count > 0)
            {
                var first = attachmentErrors[0];
                return Fail<RestoreSummary>(first.Code,
                    $"任务与设置已恢复，但有 {attachmentErrors.Count} 个附件未能恢复：{first.Message}");
            }
            return OperationResult<RestoreSummary>.Success(summary);
        }, cancellationToken);

    private async Task<OperationResult<PreparedRestore>> PrepareRestoreAsync(IReadOnlyList<BackupItemDto> items, CancellationToken cancellationToken)
    {
        var tasks = new List<TaskItemDto>();
        var settingItems = new List<BackupItemDto>();
        foreach (var item in items)
        {
            if (string.Equals(item.Kind, SyncItemKinds.Task, StringComparison.Ordinal))
            {
                TaskItemDto? task;
                try
                {
                    task = JsonSerializer.Deserialize<TaskItemDto>(item.Payload, JsonOptions);
                }
                catch (JsonException)
                {
                    return Fail<PreparedRestore>(SyncErrorCodes.SnapshotInvalid, "备份中的任务内容无法读取。");
                }
                if (task is null) return Fail<PreparedRestore>(SyncErrorCodes.SnapshotInvalid, "备份中的任务内容无法读取。");
                tasks.Add(task);
            }
            else if (string.Equals(item.Kind, SyncItemKinds.Setting, StringComparison.Ordinal))
            {
                settingItems.Add(item);
            }
        }
        var definitions = await _settings.ListDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        if (!definitions.IsSuccess)
            return Fail<PreparedRestore>(SyncErrorCodes.SettingsInvalid, "无法读取本机设置定义。");
        var byId = definitions.Value.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var item in settingItems)
        {
            if (!byId.TryGetValue(item.Key, out var definition) || !IsPortable(definition)) continue;
            if (!TryReadPayload(item.Payload, definition.Kind, out var value)) continue;
            changes[item.Key] = value;
        }
        return OperationResult<PreparedRestore>.Success(new(tasks, changes));
    }

    private async Task<OperationResult> ApplySettingsAsync(IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken)
    {
        var session = await _settings.BeginEditSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess) return Fail(SyncErrorCodes.SettingsInvalid, "无法打开设置会话。");
        await using var edit = session.Value;
        var preview = await edit.PreviewAsync(changes, 1, cancellationToken).ConfigureAwait(false);
        if (!preview.IsSuccess) return Fail(SyncErrorCodes.SettingsInvalid, preview.Error!.Message);
        if (preview.Value.Errors.Count > 0)
            return Fail(SyncErrorCodes.SettingsInvalid, "备份中的设置未通过本机校验：" + preview.Value.Errors.First().Value.Message);
        var commit = await edit.CommitAsync(1, cancellationToken).ConfigureAwait(false);
        return commit.IsSuccess ? OperationResult.Success() : Fail(SyncErrorCodes.SettingsInvalid, commit.Error!.Message);
    }

    private async Task<OperationResult<List<BackupItemDto>>> PortableSettingsAsync(CancellationToken cancellationToken)
    {
        var definitions = await _settings.ListDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        if (!definitions.IsSuccess) return Fail<List<BackupItemDto>>(SyncErrorCodes.SettingsInvalid, "无法读取本机设置定义。");
        var values = await _settings.GetValuesAsync(cancellationToken).ConfigureAwait(false);
        if (!values.IsSuccess) return Fail<List<BackupItemDto>>(SyncErrorCodes.SettingsInvalid, "无法读取本机设置。");
        var items = new List<BackupItemDto>();
        foreach (var definition in definitions.Value)
        {
            if (!IsPortable(definition)) continue;
            if (!values.Value.TryGetValue(definition.Id, out var value)) continue;
            var payload = JsonSerializer.Serialize(new SettingPayload(ToJson(value, definition.Kind)), JsonOptions);
            items.Add(new(SyncItemKinds.Setting, definition.Id, payload, 1));
        }
        return OperationResult<List<BackupItemDto>>.Success(items);
    }

    private static bool IsPortable(SettingDefinition definition) =>
        definition.Persistence == SettingPersistence.UserSettings && PortableIds.Contains(definition.Id);

    private static object? ToJson(object? value, SettingValueKind kind) => kind switch
    {
        SettingValueKind.Integer => value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture),
        SettingValueKind.Double => value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture),
        SettingValueKind.ModuleList => value is IEnumerable<string> modules ? modules.ToArray() : value,
        SettingValueKind.ProviderMap => value is IReadOnlyDictionary<string, string> map ? new Dictionary<string, string>(map, StringComparer.Ordinal) : value,
        _ => value
    };

    private static bool TryReadPayload(string payload, SettingValueKind kind, out object? value)
    {
        value = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return false;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var element)) return false;
            switch (kind)
            {
                case SettingValueKind.Boolean:
                    if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                    value = element.GetBoolean();
                    return true;
                case SettingValueKind.Integer:
                    if (!element.TryGetInt64(out var integer)) return false;
                    value = integer;
                    return true;
                case SettingValueKind.Double:
                    if (!element.TryGetDouble(out var number)) return false;
                    value = number;
                    return true;
                case SettingValueKind.Enum:
                case SettingValueKind.String:
                    if (element.ValueKind != JsonValueKind.String) return false;
                    value = element.GetString();
                    return true;
                case SettingValueKind.ModuleList:
                    if (element.ValueKind != JsonValueKind.Array) return false;
                    var modules = new List<string>();
                    foreach (var entry in element.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.String) return false;
                        modules.Add(entry.GetString()!);
                    }
                    value = modules.ToArray();
                    return true;
                case SettingValueKind.ProviderMap:
                    if (element.ValueKind != JsonValueKind.Object) return false;
                    var providers = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var entry in element.EnumerateObject())
                    {
                        if (entry.Value.ValueKind != JsonValueKind.String) return false;
                        providers[entry.Name] = entry.Value.GetString()!;
                    }
                    value = providers;
                    return true;
                default:
                    return false;
            }
        }
    }

    // 附件上传把本地集合镜像到服务器：PUT 是幂等的（同 SHA-256 服务端返回 duplicate、不重写），
    // 已在服务器上且内容一致的附件跳过，本地没有的远端附件删除。上传顺序由快照之后开始，
    // 因此快照里引用的任务一定已经存在，附件行的外键检查不会失败。
    private async Task<OperationResult<(int Ensured, int Deleted)>> UploadAttachmentsAsync(Uri server, string token, CancellationToken cancellationToken)
    {
        var local = await _tasks.ExportAttachmentsAsync(cancellationToken).ConfigureAwait(false);
        if (!local.IsSuccess) return OperationResult<(int, int)>.Failure(local.Error!);
        var remote = await _transport.ListAttachmentsAsync(server, token, cancellationToken).ConfigureAwait(false);
        if (!remote.IsSuccess) return OperationResult<(int, int)>.Failure(remote.Error!);
        var remoteById = remote.Value.Items.ToDictionary(item => item.Id);
        var ensured = 0;
        foreach (var attachment in local.Value)
        {
            if (cancellationToken.IsCancellationRequested) return Fail<(int, int)>(ErrorCodes.Canceled, "操作已取消。");
            var opened = await _tasks.OpenAttachmentAsync(attachment.Id, cancellationToken).ConfigureAwait(false);
            if (!opened.IsSuccess) return OperationResult<(int, int)>.Failure(TagAttachmentError(attachment.FileName, opened.Error!));
            using (var content = opened.Value.Content)
            {
                var upToDate = remoteById.TryGetValue(attachment.Id, out var existing) && SameAttachment(existing, attachment, opened.Value.Sha256);
                if (!upToDate)
                {
                    var uploaded = await _transport.UploadAttachmentAsync(server, token, attachment.TaskId, attachment.Id, attachment.FileName,
                        content, opened.Value.Sha256, cancellationToken).ConfigureAwait(false);
                    if (!uploaded.IsSuccess) return OperationResult<(int, int)>.Failure(TagAttachmentError(attachment.FileName, uploaded.Error!));
                }
            }
            ensured++;
        }
        var keep = local.Value.Select(item => item.Id).ToHashSet();
        var deleted = 0;
        foreach (var item in remote.Value.Items)
        {
            if (keep.Contains(item.Id)) continue;
            if (cancellationToken.IsCancellationRequested) return Fail<(int, int)>(ErrorCodes.Canceled, "操作已取消。");
            var removed = await _transport.DeleteAttachmentAsync(server, token, item.Id, cancellationToken).ConfigureAwait(false);
            if (!removed.IsSuccess) return OperationResult<(int, int)>.Failure(TagAttachmentError(item.FileName, removed.Error!));
            deleted++;
        }
        return OperationResult<(int, int)>.Success((ensured, deleted));
    }

    private static bool SameAttachment(RemoteAttachmentDto remote, TaskAttachmentDto local, string localSha256) =>
        remote.TaskId == local.TaskId &&
        string.Equals(remote.Sha256, localSha256, StringComparison.OrdinalIgnoreCase);

    private static OperationError TagAttachmentError(string fileName, OperationError error) =>
        new(error.Code, $"附件「{fileName}」同步失败：{error.Message}", MessageKey: error.MessageKey, CorrelationId: error.CorrelationId);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private async Task<OperationResult<(Uri Server, string Token)>> ContextAsync(CancellationToken cancellationToken)
    {
        if (_auth.State.Status != AuthStatus.SignedIn || _auth.State.Server is not { } server)
            return Fail<(Uri Server, string Token)>(SyncErrorCodes.NotSignedIn, "请先登录账号。");
        var token = await _auth.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return token.IsSuccess
            ? OperationResult<(Uri Server, string Token)>.Success((server, token.Value))
            : Fail<(Uri Server, string Token)>(SyncErrorCodes.NotSignedIn, "登录已过期，请重新登录。");
    }

    private async Task<OperationResult<T>> Guarded<T>(Func<Task<OperationResult<T>>> operation, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateLock) _busy = true;
            Raise();
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            lock (_stateLock) _busy = false;
            _operations.Release();
            Raise();
        }
    }

    private void Raise()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception) { /* a broken listener must not fail a backup */ }
    }

    private static OperationResult<T> Fail<T>(string code, string message) =>
        OperationResult<T>.Failure(new(code, message));

    private static OperationResult Fail(string code, string message) =>
        OperationResult.Failure(new(code, message));

    private sealed record SettingPayload(object? Value);

    private sealed record PreparedRestore(IReadOnlyList<TaskItemDto> Tasks, IReadOnlyDictionary<string, object?> Changes);
}
