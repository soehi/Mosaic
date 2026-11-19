using Mosaic.Auth.Contracts;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;

namespace Mosaic.Auth.Core.Tests;

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class Diagnostics : IDiagnosticSink
{
    public List<Exception> Reported { get; } = [];
    public void Report(string correlationId, Exception exception) => Reported.Add(exception);
}

internal sealed class Gate : IModuleExecutionGate, IDisposable
{
    private bool _active;
    public void Activate() => _active = true;
    public IDisposable? TryEnter() => _active ? this : null;
    public Task StopAsync() { _active = false; return Task.CompletedTask; }
    public void Dispose() { }
}

internal sealed class MemorySecretStore : IAuthSecretStore
{
    public AuthSecrets? Stored { get; set; }
    public int Saves { get; private set; }
    public int Clears { get; private set; }
    public bool FailSave { get; set; }
    public bool FailClear { get; set; }

    public Task<OperationResult<AuthSecrets?>> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<AuthSecrets?>.Success(Stored));

    public Task<OperationResult> SaveAsync(AuthSecrets secrets, CancellationToken cancellationToken = default)
    {
        Saves++;
        if (FailSave) return Task.FromResult(OperationResult.Failure(new(AuthErrorCodes.SecretsUnavailable, "保存失败。")));
        Stored = secrets;
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        Clears++;
        if (FailClear) return Task.FromResult(OperationResult.Failure(new(AuthErrorCodes.SecretsUnavailable, "清除失败。")));
        Stored = null;
        return Task.FromResult(OperationResult.Success());
    }
}

internal sealed class FakeSettings : ISettingsCore
{
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
    public List<SettingDefinition> Definitions { get; } = [];
    public List<IReadOnlyDictionary<string, object?>> Commits { get; } = [];
    public List<string> RegisteredModules { get; } = [];
    public bool FailEdit { get; set; }

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
        RegisteredModules.Add(source.ModuleId);
        Definitions.AddRange(source.Definitions);
        return new Registration(() =>
        {
            RegisteredModules.Remove(source.ModuleId);
            Definitions.RemoveAll(definition => definition.ModuleId == source.ModuleId);
        });
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

internal sealed class FakeTransport : IAuthTransport
{
    public int Calls { get; private set; }
    public int LogoutCalls { get; private set; }
    public int PollCalls { get; private set; }
    public int RefreshCalls { get; private set; }
    public (Uri Server, string Token, string Refresh)? LastLogout { get; private set; }
    public List<(string Method, Uri Server)> Invoked { get; } = [];

    public OperationResult<AuthPolicyDto> Policy { get; set; } =
        OperationResult<AuthPolicyDto>.Success(new(true, true, "Mosaic"));
    public OperationResult<AuthSessionDto> Session { get; set; } = OperationResult<AuthSessionDto>.Success(DefaultSession());
    public OperationResult<AuthSessionDto> RefreshResult { get; set; } = OperationResult<AuthSessionDto>.Success(DefaultSession());
    public OperationResult<DeviceAuthorizationDto> DeviceStart { get; set; } =
        OperationResult<DeviceAuthorizationDto>.Success(new("device-code", "BAADC832", new Uri("https://sync.example.com/device"), null, 60, 5));
    public OperationResult<DevicePollDto> DevicePoll { get; set; } = OperationResult<DevicePollDto>.Success(new(DeviceAuthorizationStatus.Pending, null));
    public OperationResult LogoutResult { get; set; } = OperationResult.Success();
    public OperationResult SendCodeResult { get; set; } = OperationResult.Success();

    public static AuthSessionDto DefaultSession(string accessToken = "access-1", string refreshToken = "refresh-1", int expiresIn = 3600) =>
        new(accessToken, refreshToken, expiresIn, new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "user@example.com", "用户"));

    private void Record(string method, Uri server)
    {
        Calls++;
        Invoked.Add((method, server));
    }

    public Task<OperationResult<AuthPolicyDto>> GetPolicyAsync(Uri server, CancellationToken cancellationToken = default)
    {
        Record(nameof(GetPolicyAsync), server);
        return Task.FromResult(Policy);
    }

    public Task<OperationResult> SendEmailCodeAsync(Uri server, string email, AuthEmailCodePurpose purpose, CancellationToken cancellationToken = default)
    {
        Record(nameof(SendEmailCodeAsync), server);
        return Task.FromResult(SendCodeResult);
    }

    public Task<OperationResult<AuthSessionDto>> RegisterAsync(Uri server, string email, string password, string code, CancellationToken cancellationToken = default)
    {
        Record(nameof(RegisterAsync), server);
        return Task.FromResult(Session);
    }

    public Task<OperationResult<AuthSessionDto>> LoginAsync(Uri server, string email, string password, CancellationToken cancellationToken = default)
    {
        Record(nameof(LoginAsync), server);
        return Task.FromResult(Session);
    }

    public Task<OperationResult<AuthSessionDto>> LoginWithCodeAsync(Uri server, string email, string code, CancellationToken cancellationToken = default)
    {
        Record(nameof(LoginWithCodeAsync), server);
        return Task.FromResult(Session);
    }

    public Task<OperationResult<AuthSessionDto>> RefreshAsync(Uri server, string refreshToken, CancellationToken cancellationToken = default)
    {
        RefreshCalls++;
        Record(nameof(RefreshAsync), server);
        return Task.FromResult(RefreshResult);
    }

    public Task<OperationResult> LogoutAsync(Uri server, string accessToken, string refreshToken, CancellationToken cancellationToken = default)
    {
        LogoutCalls++;
        LastLogout = (server, accessToken, refreshToken);
        Record(nameof(LogoutAsync), server);
        return Task.FromResult(LogoutResult);
    }

    public Task<OperationResult> ForgotPasswordAsync(Uri server, string email, CancellationToken cancellationToken = default)
    {
        Record(nameof(ForgotPasswordAsync), server);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> ResetPasswordAsync(Uri server, string email, string code, string password, CancellationToken cancellationToken = default)
    {
        Record(nameof(ResetPasswordAsync), server);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult<AuthAccountDto>> GetAccountAsync(Uri server, string accessToken, CancellationToken cancellationToken = default)
    {
        Record(nameof(GetAccountAsync), server);
        return Task.FromResult(OperationResult<AuthAccountDto>.Success(new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), "user@example.com", "用户")));
    }

    public Task<OperationResult<DeviceAuthorizationDto>> StartDeviceAuthorizationAsync(Uri server, string appVersion, CancellationToken cancellationToken = default)
    {
        LastAppVersion = appVersion;
        Record(nameof(StartDeviceAuthorizationAsync), server);
        return Task.FromResult(DeviceStart);
    }

    public Task<OperationResult<DevicePollDto>> PollDeviceAuthorizationAsync(Uri server, string deviceCode, CancellationToken cancellationToken = default)
    {
        PollCalls++;
        Record(nameof(PollDeviceAuthorizationAsync), server);
        return Task.FromResult(DevicePoll);
    }

    public string? LastAppVersion { get; private set; }
    public OperationResult<IReadOnlyList<AuthSessionInfoDto>> Sessions { get; set; } =
        OperationResult<IReadOnlyList<AuthSessionInfoDto>>.Success([]);
    public OperationResult RevokeResult { get; set; } = OperationResult.Success();
    public OperationResult DeleteResult { get; set; } = OperationResult.Success();
    public (string? Password, string? Code)? LastDelete { get; private set; }

    public Task<OperationResult<IReadOnlyList<AuthSessionInfoDto>>> ListSessionsAsync(Uri server, string accessToken, CancellationToken cancellationToken = default)
    {
        Record(nameof(ListSessionsAsync), server);
        return Task.FromResult(Sessions);
    }

    public Task<OperationResult> RevokeSessionAsync(Uri server, string accessToken, string sessionId, CancellationToken cancellationToken = default)
    {
        Record(nameof(RevokeSessionAsync), server);
        return Task.FromResult(RevokeResult);
    }

    public Task<OperationResult> RevokeAllSessionsAsync(Uri server, string accessToken, bool keepCurrent, CancellationToken cancellationToken = default)
    {
        Record(nameof(RevokeAllSessionsAsync), server);
        return Task.FromResult(RevokeResult);
    }

    public Task<OperationResult> DeleteAccountAsync(Uri server, string accessToken, string? password, string? code, CancellationToken cancellationToken = default)
    {
        LastDelete = (password, code);
        Record(nameof(DeleteAccountAsync), server);
        return Task.FromResult(DeleteResult);
    }
}
