using Mosaic.Contracts;
using Mosaic.Settings.Contracts;

namespace Mosaic.Wpf.Tests;

internal sealed class TestSettingsRepository : ISettingRepository
{
    internal Dictionary<string, object?> Values = [];
    internal int Writes;
    public Task<OperationResult<IReadOnlyDictionary<string, object?>>> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<IReadOnlyDictionary<string, object?>>.Success(new Dictionary<string, object?>(Values)));
    public Task<OperationResult> SaveAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    { Writes++; foreach (var item in values) Values[item.Key] = item.Value; return Task.FromResult(OperationResult.Success()); }
    public Task<OperationResult> DeleteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

// UI-only fake: core preview routing and compensation are exercised with the real service separately.
internal sealed class TestSettingsSession(ISettingsApi api, IReadOnlyDictionary<string, object?> baseline) : ISettingsEditSession
{
    private readonly Dictionary<string, object?> _saved = new(baseline);
    private IReadOnlyDictionary<string, object?> _draft = new Dictionary<string, object?>();
    public IReadOnlyDictionary<string, object?> SavedValues => new Dictionary<string, object?>(_saved);
    internal static async Task<OperationResult<ISettingsEditSession>> OpenAsync(ISettingsApi api)
    {
        var values = (await api.ListDefinitionsAsync()).Value.ToDictionary(item => item.Id, item => item.DefaultValue);
        foreach (var item in (await api.GetValuesAsync()).Value) values[item.Key] = item.Value;
        return OperationResult<ISettingsEditSession>.Success(new TestSettingsSession(api, values));
    }
    public Task<OperationResult<SettingPreviewResult>> PreviewAsync(IReadOnlyDictionary<string, object?> changes, long revision, CancellationToken cancellationToken = default)
    {
        _draft = new Dictionary<string, object?>(changes);
        return Task.FromResult(OperationResult<SettingPreviewResult>.Success(new(revision, new Dictionary<string, OperationError>(),
            new Dictionary<string, OperationError>(), changes.Keys.ToArray(), [])));
    }
    public async Task<OperationResult<SettingsCommitResult>> CommitAsync(long expectedRevision, CancellationToken cancellationToken = default)
    {
        var result = await api.ProposeChangesAsync(_draft, cancellationToken);
        if (!result.IsSuccess) return OperationResult<SettingsCommitResult>.Failure(result.Error!);
        if (result.Value.Any(item => item.Key != SettingResults.ApplyWarningId)) return OperationResult<SettingsCommitResult>.Failure(result.Value.First().Value);
        foreach (var item in _draft) _saved[item.Key] = item.Value;
        _draft = new Dictionary<string, object?>();
        return OperationResult<SettingsCommitResult>.Success(new(result.Value));
    }
    public Task<OperationResult> DiscardAsync(CancellationToken cancellationToken = default)
    { _draft = new Dictionary<string, object?>(); return Task.FromResult(OperationResult.Success()); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
