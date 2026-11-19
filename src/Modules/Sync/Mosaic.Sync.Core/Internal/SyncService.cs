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

    internal void RaiseForAuthChange() => Raise();

    public event EventHandler? StateChanged;

    public SyncState State
    {
        get
        {
            lock (_stateLock)
                return new(_auth.State.Status == AuthStatus.SignedIn, _busy, _latest, _lastRestore, _messageCode);
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
            Raise();
            return OperationResult<BackupUploadResult>.Success(new(uploaded.Value.SnapshotId, exported.Value.Count, settingsCount, uploaded.Value.TotalBytes, uploaded.Value.ServerRevision, uploaded.Value.Cursor));
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
            var replaced = await _tasks.ReplaceAllAsync(prepared.Value.Tasks, cancellationToken).ConfigureAwait(false);
            if (!replaced.IsSuccess)
                return Fail<RestoreSummary>(SyncErrorCodes.TasksImportFailed, "本地任务未能被备份覆盖：" + replaced.Error!.Message);
            var summary = new RestoreSummary(prepared.Value.Tasks.Count, 0);
            if (prepared.Value.Changes.Count > 0)
            {
                var applied = await ApplySettingsAsync(prepared.Value.Changes, cancellationToken).ConfigureAwait(false);
                if (!applied.IsSuccess) return OperationResult<RestoreSummary>.Failure(applied.Error!);
                summary = new(prepared.Value.Tasks.Count, prepared.Value.Changes.Count);
            }
            lock (_stateLock)
            {
                _latest = snapshot;
                _lastRestore = summary;
                _messageCode = null;
            }
            Raise();
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
