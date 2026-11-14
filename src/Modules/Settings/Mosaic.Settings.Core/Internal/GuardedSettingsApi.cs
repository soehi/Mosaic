using Mosaic.Contracts;
using Mosaic.Settings.Contracts;

namespace Mosaic.Settings.Core.Internal;

internal sealed class GuardedSettingsApi(ISettingsCore inner, IModuleExecutionGate? gate, IDiagnosticSink? diagnostics) : ISettingsCore
{
    public async Task<OperationResult<IReadOnlyList<SettingDefinition>>> ListDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        if (!TryEnter(out var lease)) return Unavailable<IReadOnlyList<SettingDefinition>>();
        using (lease)
        {
            try { return await inner.ListDefinitionsAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) { return Report<IReadOnlyList<SettingDefinition>>(exception); }
        }
    }

    public async Task<OperationResult<IReadOnlyDictionary<string, object?>>> GetValuesAsync(CancellationToken cancellationToken = default)
    {
        if (!TryEnter(out var lease)) return Unavailable<IReadOnlyDictionary<string, object?>>();
        using (lease)
        {
            try { return await inner.GetValuesAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) { return Report<IReadOnlyDictionary<string, object?>>(exception); }
        }
    }

    public async Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
        IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default)
    {
        if (!TryEnter(out var lease)) return Unavailable<IReadOnlyDictionary<string, OperationError>>();
        using (lease)
        {
            try { return await inner.ProposeChangesAsync(changes, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) { return Report<IReadOnlyDictionary<string, OperationError>>(exception); }
        }
    }

    public async Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken cancellationToken = default)
    {
        if (!TryEnter(out var lease)) return UnavailableReset();
        using (lease)
        {
            try { return await inner.ResetAsync(moduleId, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) { return ReportReset(exception); }
        }
    }

    public IDisposable Register(ISettingSource source) => inner.Register(source);

    private bool TryEnter(out IDisposable? lease)
    {
        lease = gate?.TryEnter();
        return lease is not null || gate is null;
    }

    private static OperationResult<T> Unavailable<T>() =>
        OperationResult<T>.Failure(new(ErrorCodes.CapabilityUnavailable, "设置能力当前不可用。"));

    private static OperationResult UnavailableReset() =>
        OperationResult.Failure(new(ErrorCodes.CapabilityUnavailable, "设置能力当前不可用。"));

    private OperationResult ReportReset(Exception exception)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        try { diagnostics?.Report(correlationId, exception); } catch { }
        return OperationResult.Failure(new(ErrorCodes.InternalError, "设置操作失败。", null, correlationId));
    }

    private OperationResult<T> Report<T>(Exception exception)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        try { diagnostics?.Report(correlationId, exception); } catch { }
        return OperationResult<T>.Failure(new(ErrorCodes.InternalError, "设置操作失败。", null, correlationId));
    }
}
