using Mosaic.Auth.Contracts;
using Mosaic.Contracts;

namespace Mosaic.Auth.Core.Internal;

// The module gate keeps account calls unavailable while the module is starting or stopping; the
// UI therefore never observes a half-initialized service.
internal sealed class GuardedAuthApi(AuthService service, IModuleExecutionGate gate, IDiagnosticSink diagnostics) : IAuthApi
{
    public AuthState State => service.State;
    public Guid DeviceId => service.DeviceId;

    public event EventHandler? StateChanged
    {
        add => service.StateChanged += value;
        remove => service.StateChanged -= value;
    }

    private async Task<OperationResult<T>> Invoke<T>(Func<CancellationToken, Task<OperationResult<T>>> operation, CancellationToken cancellationToken)
    {
        using var lease = gate.TryEnter();
        if (lease is null) return OperationResult<T>.Failure(new(ErrorCodes.CapabilityUnavailable, "账号功能尚未启用或正在关闭。"));
        if (cancellationToken.IsCancellationRequested) return OperationResult<T>.Failure(new(ErrorCodes.Canceled, "操作已取消。"));
        try { return await operation(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return OperationResult<T>.Failure(new(ErrorCodes.Canceled, "操作已取消。")); }
        catch (Exception ex) { return OperationResult<T>.Failure(Report(ex)); }
    }

    private async Task<OperationResult> Invoke(Func<CancellationToken, Task<OperationResult>> operation, CancellationToken cancellationToken)
    {
        var result = await Invoke<object?>(async token =>
        {
            var inner = await operation(token).ConfigureAwait(false);
            return inner.IsSuccess ? OperationResult<object?>.Success(null) : OperationResult<object?>.Failure(inner.Error!);
        }, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(result.Error!);
    }

    private OperationError Report(Exception exception)
    {
        var id = Guid.NewGuid().ToString("N");
        diagnostics.Report(id, exception);
        return new(ErrorCodes.InternalError, "操作未完成，请重试。", CorrelationId: id);
    }

    public Task<OperationResult<AuthPolicyDto>> GetPolicyAsync(CancellationToken cancellationToken = default) =>
        Invoke(service.GetPolicyAsync, cancellationToken);

    public Task<OperationResult> SetServerAsync(string serverUrl, CancellationToken cancellationToken = default) =>
        Invoke(token => service.SetServerAsync(serverUrl, token), cancellationToken);

    public Task<OperationResult> RequestEmailCodeAsync(string email, AuthEmailCodePurpose purpose, CancellationToken cancellationToken = default) =>
        Invoke(token => service.RequestEmailCodeAsync(email, purpose, token), cancellationToken);

    public Task<OperationResult> RegisterAsync(string email, string code, string password, CancellationToken cancellationToken = default) =>
        Invoke(token => service.RegisterAsync(email, code, password, token), cancellationToken);

    public Task<OperationResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default) =>
        Invoke(token => service.LoginAsync(email, password, token), cancellationToken);

    public Task<OperationResult> LoginWithCodeAsync(string email, string code, CancellationToken cancellationToken = default) =>
        Invoke(token => service.LoginWithCodeAsync(email, code, token), cancellationToken);

    public Task<OperationResult> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) =>
        Invoke(token => service.ForgotPasswordAsync(email, token), cancellationToken);

    public Task<OperationResult> ResetPasswordAsync(string email, string code, string newPassword, CancellationToken cancellationToken = default) =>
        Invoke(token => service.ResetPasswordAsync(email, code, newPassword, token), cancellationToken);

    public Task<OperationResult<DeviceAuthorizationDto>> StartDeviceAuthorizationAsync(CancellationToken cancellationToken = default) =>
        Invoke(service.StartDeviceAuthorizationAsync, cancellationToken);

    public Task<OperationResult<DeviceAuthorizationStatus>> PollDeviceAuthorizationAsync(CancellationToken cancellationToken = default) =>
        Invoke(service.PollDeviceAuthorizationAsync, cancellationToken);

    public Task<OperationResult> CancelDeviceAuthorizationAsync() =>
        Invoke(_ => service.CancelDeviceAuthorizationAsync(), CancellationToken.None);

    public Task<OperationResult> LogoutAsync(CancellationToken cancellationToken = default) =>
        Invoke(service.LogoutAsync, cancellationToken);

    public Task<OperationResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        Invoke(service.GetAccessTokenAsync, cancellationToken);
}
