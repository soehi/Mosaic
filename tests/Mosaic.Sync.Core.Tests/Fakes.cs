using Mosaic.Auth.Contracts;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Sync.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Sync.Core.Tests;

internal sealed class Gate : IModuleExecutionGate, IDisposable
{
    private bool _active;
    public void Activate() => _active = true;
    public IDisposable? TryEnter() => _active ? this : null;
    public Task StopAsync() { _active = false; return Task.CompletedTask; }
    public void Dispose() { }
}

internal sealed class Diagnostics : IDiagnosticSink
{
    public List<Exception> Reported { get; } = [];
    public void Report(string correlationId, Exception exception) => Reported.Add(exception);
}

internal sealed class FakeAuthApi : IAuthApi
{
    public AuthState State { get; set; } = new(AuthStatus.SignedIn,
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "user@example.com", "用户"),
        new Uri("https://sync.example.com/"));

    public Guid DeviceId { get; set; } = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public event EventHandler? StateChanged;

    public void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);

    public Task<OperationResult<AuthPolicyDto>> GetPolicyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<AuthPolicyDto>.Success(new(true, true, "Mosaic")));

    public Task<OperationResult> SetServerAsync(string serverUrl, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());

    public Task<OperationResult> RequestEmailCodeAsync(string email, AuthEmailCodePurpose purpose, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());

    public Task<OperationResult> RegisterAsync(string email, string code, string password, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());

    public Task<OperationResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());

    public Task<OperationResult> LoginWithCodeAsync(string email, string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());

    public Task<OperationResult> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());

    public Task<OperationResult> ResetPasswordAsync(string email, string code, string newPassword, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());

    public Task<OperationResult<DeviceAuthorizationDto>> StartDeviceAuthorizationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<DeviceAuthorizationDto>.Failure(new(AuthErrorCodes.ServerRequired, "请先填写服务器地址。")));

    public Task<OperationResult<DevicePollDto>> PollDeviceAuthorizationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<DevicePollDto>.Success(new(DeviceAuthorizationStatus.Pending, null)));

    public Task<OperationResult> CancelDeviceAuthorizationAsync() => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> LogoutAsync(CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());

    public Task<OperationResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<string>.Success("access-token"));

    public Task<OperationResult<IReadOnlyList<AuthSessionInfoDto>>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<IReadOnlyList<AuthSessionInfoDto>>.Success([]));

    public Task<OperationResult> RevokeSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());

    public Task<OperationResult> RevokeAllSessionsAsync(bool keepCurrent, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());

    public Task<OperationResult> DeleteAccountAsync(string? password, string? code, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());
}

internal sealed class FakeSyncTransport : ISyncTransport
{
    public int Uploads { get; private set; }
    public int DownloadRequests { get; private set; }
    public IReadOnlyList<BackupItemDto>? LastUpload { get; private set; }
    public Guid LastDeviceId { get; private set; }
    public string LastToken { get; private set; } = "";

    public OperationResult<BackupSnapshotDto> UploadResult { get; set; } =
        OperationResult<BackupSnapshotDto>.Success(Snapshot("snap-1"));
    public OperationResult<BackupSnapshotDto?> LatestResult { get; set; } =
        OperationResult<BackupSnapshotDto?>.Success(Snapshot("snap-1"));
    public OperationResult<BackupSnapshotContentDto> DownloadResult { get; set; } =
        OperationResult<BackupSnapshotContentDto>.Success(new(Snapshot("snap-1"), []));

    public static BackupSnapshotDto Snapshot(string id) =>
        new(id, new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero), 3, "sha", 1024);

    public Task<OperationResult<BackupSnapshotDto>> UploadAsync(
        Uri server, string accessToken, Guid deviceId, IReadOnlyList<BackupItemDto> items, CancellationToken cancellationToken = default)
    {
        Uploads++;
        LastUpload = items;
        LastDeviceId = deviceId;
        LastToken = accessToken;
        return Task.FromResult(UploadResult);
    }

    public Task<OperationResult<BackupSnapshotDto?>> GetLatestAsync(Uri server, string accessToken, CancellationToken cancellationToken = default)
    {
        LastToken = accessToken;
        return Task.FromResult(LatestResult);
    }

    public Task<OperationResult<BackupSnapshotContentDto>> DownloadAsync(
        Uri server, string accessToken, string snapshotId, CancellationToken cancellationToken = default)
    {
        DownloadRequests++;
        LastToken = accessToken;
        return Task.FromResult(DownloadResult);
    }
}

internal sealed class FakeTaskTransferApi : ITaskTransferApi
{
    public List<TaskItemDto> Tasks { get; } = [];
    public IReadOnlyList<TaskItemDto>? Replaced { get; private set; }
    public OperationResult ReplaceResult { get; set; } = OperationResult.Success();

    public Task<OperationResult<IReadOnlyList<TaskItemDto>>> ExportAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success(Tasks.ToArray()));

    public Task<OperationResult> ReplaceAllAsync(IReadOnlyList<TaskItemDto> tasks, CancellationToken cancellationToken = default)
    {
        if (!ReplaceResult.IsSuccess) return Task.FromResult(ReplaceResult);
        Replaced = tasks;
        Tasks.Clear();
        Tasks.AddRange(tasks);
        return Task.FromResult(OperationResult.Success());
    }
}

internal sealed class FakeSettings : ISettingsCore
{
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
    public List<SettingDefinition> Definitions { get; } = [];
    public List<ISettingSource> Sources { get; } = [];
    public List<IReadOnlyDictionary<string, object?>> Commits { get; } = [];
    public bool FailEdit { get; set; }

    public static SettingDefinition Definition(string id, SettingPersistence persistence, SettingValueKind kind = SettingValueKind.String) =>
        new(id, "test", id, id, kind, null, null, null, null, SettingsGroup.Tasks, SettingCategory.Normal,
            SettingApplyMode.Immediate, persistence, DisplayNameKey: "Settings.Setting." + id);

    public Task<OperationResult<IReadOnlyList<SettingDefinition>>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<IReadOnlyList<SettingDefinition>>.Success(Definitions.ToArray()));

    public Task<OperationResult<IReadOnlyDictionary<string, object?>>> GetValuesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<IReadOnlyDictionary<string, object?>>.Success(new Dictionary<string, object?>(Values, StringComparer.Ordinal)));

    public Task<OperationResult<ISettingsEditSession>> BeginEditSessionAsync(CancellationToken cancellationToken = default) =>
        FailEdit
            ? Task.FromResult(OperationResult<ISettingsEditSession>.Failure(new(ErrorCodes.CapabilityUnavailable, "设置编辑不可用。")))
            : Task.FromResult(OperationResult<ISettingsEditSession>.Success(new Session(this)));

    public Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
        IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<IReadOnlyDictionary<string, OperationError>>.Success(new Dictionary<string, OperationError>()));

    public Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());

    public Task<OperationResult> ResetAsync(IReadOnlyList<string> definitionIds, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());

    public IDisposable Register(ISettingSource source)
    {
        Sources.Add(source);
        Definitions.AddRange(source.Definitions.Where(definition => Definitions.All(existing => existing.Id != definition.Id)));
        return new Registration(() =>
        {
            Sources.Remove(source);
            Definitions.RemoveAll(definition => definition.ModuleId == source.ModuleId);
        });
    }

    public async Task ApplySourcesAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = new Dictionary<string, object?>(Values, StringComparer.Ordinal);
        foreach (var source in Sources.ToArray()) await source.ApplyAsync(snapshot, cancellationToken);
    }

    private sealed class Registration(Action onDispose) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose(); }
    }

    private sealed class Session(FakeSettings owner) : ISettingsEditSession
    {
        private readonly Dictionary<string, object?> _pending = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, object?> SavedValues => owner.Values;

        public Task<OperationResult<SettingPreviewResult>> PreviewAsync(
            IReadOnlyDictionary<string, object?> cumulativeChanges, long revision, CancellationToken cancellationToken = default)
        {
            foreach (var (key, value) in cumulativeChanges) _pending[key] = value;
            return Task.FromResult(OperationResult<SettingPreviewResult>.Success(new(
                revision,
                new Dictionary<string, OperationError>(),
                new Dictionary<string, OperationError>(),
                [],
                [])));
        }

        public Task<OperationResult<SettingsCommitResult>> CommitAsync(long expectedRevision, CancellationToken cancellationToken = default)
        {
            owner.Commits.Add(new Dictionary<string, object?>(_pending, StringComparer.Ordinal));
            foreach (var (key, value) in _pending) owner.Values[key] = value;
            _pending.Clear();
            return Task.FromResult(OperationResult<SettingsCommitResult>.Success(new(new Dictionary<string, OperationError>())));
        }

        public Task<OperationResult> DiscardAsync(CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
