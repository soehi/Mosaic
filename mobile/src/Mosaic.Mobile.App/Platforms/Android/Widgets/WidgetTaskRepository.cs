using Mosaic.Mobile.App.Services;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Presentation;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;

namespace Mosaic.Mobile.App.Platforms.Android.Widgets;

/// <summary>
/// widget 广播的冷启动会话与身份边界。它只复用共享 DI 容器、共享会话和
/// 投影仓库，不创建独立 HttpClient、令牌存储或本地写入路径。
/// </summary>
public sealed class WidgetTaskRepository
{
    private readonly AuthSessionManager _auth;
    private readonly WidgetTaskReader _reader;
    private readonly TaskWriteClient _writes;
    private readonly LocalizationService _localization;
    private readonly AppPreferences _preferences;

    public WidgetTaskRepository(
        AuthSessionManager auth,
        WidgetTaskReader reader,
        TaskWriteClient writes,
        LocalizationService localization,
        AppPreferences preferences)
    {
        _auth = auth;
        _reader = reader;
        _writes = writes;
        _localization = localization;
        _preferences = preferences;
    }

    public async Task<WidgetContext?> EstablishAsync(CancellationToken cancellationToken = default)
    {
        var data = _preferences.Load();
        if (!ServerAddress.TryNormalize(data.ServerUrl, out var serverUrl, out _))
        {
            return null;
        }

        if (_auth.State == AuthState.Unknown
            || !string.Equals(_auth.ServerUrl, serverUrl, StringComparison.Ordinal))
        {
            var result = await _auth.InitializeAsync(serverUrl, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded
                || _auth.State != AuthState.SignedIn
                || _auth.Generation == 0
                || string.IsNullOrEmpty(_auth.DeviceId))
            {
                return null;
            }
        }

        if (!_reader.IsCurrent(_auth.Generation))
        {
            return null;
        }

        var verified = _auth.VerifiedUserId;
        if (string.IsNullOrWhiteSpace(verified))
        {
            var verification = await _auth.VerifyIdentityAsync().ConfigureAwait(false);
            if (verification.Status != IdentityVerificationStatus.Verified
                || string.IsNullOrWhiteSpace(verification.UserId))
            {
                return null;
            }

            verified = verification.UserId;
        }

        if (!_reader.IsCurrent(_auth.Generation))
        {
            return null;
        }

        return new WidgetContext(
            serverUrl,
            verified,
            _auth.Generation,
            _auth.DeviceId ?? string.Empty);
    }

    public bool IsCurrent(long generation) => _reader.IsCurrent(generation);

    public Task<WidgetSnapshot> ReadAsync(
        WidgetContext context,
        int maxRows,
        CancellationToken cancellationToken = default) =>
        _reader.ReadAsync(context.ServerUrl, maxRows, context.Generation, cancellationToken);

    public Task<SyncedTask?> FindTaskAsync(
        Guid taskId,
        WidgetContext context,
        CancellationToken cancellationToken = default) =>
        _reader.FindTaskAsync(taskId, context.ServerUrl, context.Generation, cancellationToken);

    public async Task<WidgetWriteResult> WriteAsync(
        SyncedTask task,
        WidgetTaskAction action,
        WidgetContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_reader.IsCurrent(context.Generation) || string.IsNullOrWhiteSpace(context.DeviceId))
        {
            return new WidgetWriteResult(WidgetWriteStatus.SessionExpired);
        }

        var result = await _writes
            .WriteAsync(task, action, context.DeviceId, cancellationToken)
            .ConfigureAwait(false);
        return WidgetWriteText.From(result);
    }

    public async Task ExpireSessionAsync()
    {
        if (_auth.State is AuthState.SignedIn or AuthState.SessionExpired)
        {
            await _auth.ExpireForWidgetAsync().ConfigureAwait(false);
        }
    }

    public string Describe(WidgetWriteResult result) => WidgetWriteText.Describe(_localization, result);

    public string Text(string key) => _localization.T(key);

    public string Format(string key, params object?[] arguments) => _localization.Format(key, arguments);
}

public sealed record WidgetContext(
    string ServerUrl,
    string UserId,
    long Generation,
    string DeviceId);
