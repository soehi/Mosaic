using System.Collections.ObjectModel;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;

namespace Mosaic.Settings.Core.Internal;

internal sealed partial class SettingsService
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Dictionary<string, object?> _running = new(StringComparer.Ordinal);
    private readonly HashSet<string> _preparedSources = new(StringComparer.Ordinal);
    private EditSession? _session;
    private bool _stopping;
    private ISettingSource[] Sources { get { lock (_gate) return _sources.Where(source => !_preparedSources.Contains(source.ModuleId)).ToArray(); } }
    private static OperationError Conflict() => new(ErrorCodes.Conflict, "设置正在另一编辑会话中使用。");
    private static OperationError Closed() => new(ErrorCodes.CapabilityUnavailable, "设置编辑会话已结束。");
    private static Dictionary<string, object?> Copy(IReadOnlyDictionary<string, object?> values) => values.ToDictionary(
        item => item.Key, item => item.Value switch
        {
            string[] items => (object?)items.ToArray(),
            IReadOnlyDictionary<string, string> map => new Dictionary<string, string>(map, StringComparer.Ordinal),
            _ => item.Value
        }, StringComparer.Ordinal);
    private Dictionary<string, object?> ImmediateValues(IReadOnlyDictionary<string, object?> values) =>
        Definitions.Where(item => item.ApplyMode == SettingApplyMode.Immediate && item.Persistence == SettingPersistence.UserSettings)
            .Where(item => values.ContainsKey(item.Id)).ToDictionary(item => item.Id, item => values[item.Id], StringComparer.Ordinal);

    public async Task<OperationResult<ISettingsEditSession>> BeginEditSessionAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping) return OperationResult<ISettingsEditSession>.Failure(Closed());
            if (_session is not null) return OperationResult<ISettingsEditSession>.Failure(Conflict());
            var saved = await GetValuesAsync(cancellationToken).ConfigureAwait(false);
            if (!saved.IsSuccess) return OperationResult<ISettingsEditSession>.Failure(saved.Error!);
            foreach (var (id, value) in ImmediateValues(saved.Value)) _running.TryAdd(id, value);
            _session = new(this, Copy(saved.Value), Copy(_running));
            return OperationResult<ISettingsEditSession>.Success(_session);
        }
        finally { _operations.Release(); }
    }

    public async Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
        IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping || _session is not null) return OperationResult<IReadOnlyDictionary<string, OperationError>>.Failure(Conflict());
            return await ProposeChangesCoreAsync(changes, cancellationToken).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    public async Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken cancellationToken = default)
    {
        var result = await ResetWithResultAsync(moduleId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(result.Error!);
    }
    public async Task<OperationResult> ResetAsync(IReadOnlyList<string> definitionIds, CancellationToken cancellationToken = default)
    {
        var result = await ResetWithResultAsync(definitionIds, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(result.Error!);
    }
    public Task<OperationResult<SettingsCommitResult>> ResetWithResultAsync(
        string? moduleId = null, CancellationToken cancellationToken = default) =>
        ResetExclusiveAsync(() => ResetCoreAsync(moduleId, cancellationToken), cancellationToken);
    public Task<OperationResult<SettingsCommitResult>> ResetWithResultAsync(
        IReadOnlyList<string> definitionIds, CancellationToken cancellationToken = default) =>
        ResetExclusiveAsync(() => ResetCoreAsync(definitionIds, cancellationToken), cancellationToken);
    private async Task<OperationResult<SettingsCommitResult>> ResetExclusiveAsync(
        Func<Task<OperationResult<SettingsCommitResult>>> reset, CancellationToken token)
    {
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return _session is not null || _stopping
                ? OperationResult<SettingsCommitResult>.Failure(Conflict())
                : await reset().ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private async Task<Dictionary<string, OperationError>> ValidateCandidateAsync(Dictionary<string, object?> candidate, CancellationToken token)
    {
        var errors = new Dictionary<string, OperationError>(StringComparer.Ordinal);
        foreach (var (id, value) in candidate.ToArray())
        {
            var definition = Find(id);
            if (definition is null) { errors[id] = new(ErrorCodes.InvalidRequest, "设置来源已撤销。", id); continue; }
            candidate[id] = SettingValueNormalizer.Normalize(definition, value);
            if (Validate(definition, candidate[id]) is { } error) errors[id] = error;
        }
        if (errors.Count != 0) return errors;
        foreach (var source in Sources.Cast<ISettingSource>().Concat(hostSource is null ? [] : [hostSource]))
        {
            var result = await source.ValidateAsync(candidate, token).ConfigureAwait(false);
            if (!result.IsSuccess) errors[result.Error!.Field ?? source.ModuleId] = result.Error;
        }
        return errors;
    }

    private async Task<OperationResult> ApplyPreviewAsync(Dictionary<string, object?> next, CancellationToken token)
    {
        var previous = Copy(_running);
        var attempted = new List<ISettingSource>();
        try
        {
            foreach (var source in Sources.Where(source => source.Definitions.Any(item => item is { ApplyMode: SettingApplyMode.Immediate, PreviewMode: SettingPreviewMode.Reversible, Persistence: SettingPersistence.UserSettings })))
            {
                attempted.Add(source); // Include partial changes in the failing source when compensating.
                await source.ApplyAsync(next, token).ConfigureAwait(false);
            }
            _running.Clear(); foreach (var item in next) _running[item.Key] = item.Value;
            return OperationResult.Success();
        }
        catch (Exception)
        {
            var failed = false;
            foreach (var source in attempted.AsEnumerable().Reverse())
            {
                try { await source.ApplyAsync(previous, CancellationToken.None).ConfigureAwait(false); }
                catch { failed = true; }
            }
            if (failed && _session is { } session) session.Unconfirmed = true;
            return OperationResult.Failure(new(ErrorCodes.InternalError,
                failed ? "预览回滚失败，运行状态未确认。请重试放弃修改。" : "无法应用本次预览，已恢复上次有效显示。"));
        }
    }

    public async Task<OperationResult> PrepareSourceStopAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        _session?.CancelPreview();
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = Sources.FirstOrDefault(source => source.ModuleId == moduleId);
            if (source is null || _session is null) return OperationResult.Success();
            try { await source.ApplyAsync(_session.BaseRuntime, CancellationToken.None).ConfigureAwait(false); }
            catch { _session.Unconfirmed = true; return OperationResult.Failure(new(ErrorCodes.InternalError, "来源停止前回滚失败。")); }
            var ids = source.Definitions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            _session.Remove(ids);
            foreach (var id in ids) _running.Remove(id);
            lock (_gate) _preparedSources.Add(moduleId);
            return OperationResult.Success();
        }
        finally { _operations.Release(); }
    }

    internal async Task StopEditingAsync()
    {
        _stopping = true;
        if (_session is { } session) await session.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class EditSession(SettingsService owner, Dictionary<string, object?> saved, Dictionary<string, object?> runtime) : ISettingsEditSession
    {
        private Dictionary<string, object?> _saved = saved;
        internal Dictionary<string, object?> BaseRuntime = runtime;
        private Dictionary<string, object?> _draft = [];
        private long _revision = -1;
        private CancellationTokenSource? _applying;
        private bool _ended;
        private bool _hasPreview;
        internal bool Unconfirmed;
        public IReadOnlyDictionary<string, object?> SavedValues => new ReadOnlyDictionary<string, object?>(Copy(_saved));
        internal void CancelPreview() { try { _applying?.Cancel(); } catch (ObjectDisposedException) { } }
        internal void Remove(HashSet<string> ids)
        {
            _saved = _saved.Where(item => !ids.Contains(item.Key)).ToDictionary();
            _draft = _draft.Where(item => !ids.Contains(item.Key)).ToDictionary();
            BaseRuntime = BaseRuntime.Where(item => !ids.Contains(item.Key)).ToDictionary();
        }

        public async Task<OperationResult<SettingPreviewResult>> PreviewAsync(IReadOnlyDictionary<string, object?> cumulativeChanges,
            long revision, CancellationToken cancellationToken = default)
        {
            await owner._operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_ended || owner._stopping) return OperationResult<SettingPreviewResult>.Failure(Closed());
                if (revision < _revision) return OperationResult<SettingPreviewResult>.Failure(new(ErrorCodes.Conflict, "预览代次已过期。"));
                _revision = revision; _draft = Copy(cumulativeChanges);
                var candidate = Copy(_saved); foreach (var item in _draft) candidate[item.Key] = item.Value;
                using var applying = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); _applying = applying;
                var errors = await owner.ValidateCandidateAsync(candidate, applying.Token).ConfigureAwait(false);
                var applied = new List<string>();
                var saveOnly = new List<string>();
                foreach (var id in _draft.Keys)
                {
                    var definition = owner.Find(id);
                    if (definition is { ApplyMode: SettingApplyMode.Immediate, PreviewMode: SettingPreviewMode.Reversible, Persistence: SettingPersistence.UserSettings }) applied.Add(id);
                    else saveOnly.Add(id);
                }
                if (errors.Count == 0)
                {
                    var next = Copy(owner._running);
                    foreach (var definition in owner.Definitions.Where(item => item.ApplyMode == SettingApplyMode.Immediate && item.PreviewMode == SettingPreviewMode.Reversible && item.Persistence == SettingPersistence.UserSettings))
                        if (candidate.TryGetValue(definition.Id, out var value)) next[definition.Id] = value;
                    var result = await owner.ApplyPreviewAsync(next, applying.Token).ConfigureAwait(false);
                    if (!result.IsSuccess) errors["settings.preview"] = result.Error!;
                    else { Unconfirmed = false; _hasPreview = true; }
                }
                return OperationResult<SettingPreviewResult>.Success(new(revision, errors, new Dictionary<string, OperationError>(),
                    errors.Count == 0 ? applied : [], saveOnly));
            }
            catch (Exception) { return OperationResult<SettingPreviewResult>.Failure(new(ErrorCodes.InternalError, "预览未完成，输入已保留。")); }
            finally { _applying = null; owner._operations.Release(); }
        }

        public async Task<OperationResult<SettingsCommitResult>> CommitAsync(long expectedRevision, CancellationToken cancellationToken = default)
        {
            await owner._operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_ended || owner._stopping) return OperationResult<SettingsCommitResult>.Failure(Closed());
                if (expectedRevision != _revision) return OperationResult<SettingsCommitResult>.Failure(new(ErrorCodes.Conflict, "草稿已更新，请等待最新预览。"));
                var candidate = Copy(_saved); foreach (var item in _draft) candidate[item.Key] = item.Value;
                var errors = await owner.ValidateCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (errors.Count > 0) return OperationResult<SettingsCommitResult>.Failure(errors.First().Value);
                var result = await owner.ProposeChangesCoreAsync(_draft, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess) return OperationResult<SettingsCommitResult>.Failure(result.Error!);
                if (result.Value.Any(item => item.Key is not SettingResults.ApplyWarningId and not SettingResults.CommitWarningId))
                    return OperationResult<SettingsCommitResult>.Failure(result.Value.First().Value);
                _saved = candidate; _draft.Clear(); BaseRuntime = Copy(owner._running); Unconfirmed = false; _hasPreview = false;
                return OperationResult<SettingsCommitResult>.Success(new(result.Value));
            }
            catch (Exception) { return OperationResult<SettingsCommitResult>.Failure(new(ErrorCodes.InternalError, "提交设置失败，草稿已保留。")); }
            finally { owner._operations.Release(); }
        }

        public async Task<OperationResult> DiscardAsync(CancellationToken cancellationToken = default)
        {
            await owner._operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_ended) return OperationResult.Success();
                if (!_hasPreview && !Unconfirmed && _draft.Count == 0) return OperationResult.Success();
                var restored = await owner.ApplyPreviewAsync(Copy(BaseRuntime), CancellationToken.None).ConfigureAwait(false);
                if (!restored.IsSuccess) { Unconfirmed = true; return restored; }
                _draft.Clear(); Unconfirmed = false; _hasPreview = false;
                return OperationResult.Success();
            }
            finally { owner._operations.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            CancelPreview();
            var restored = await DiscardAsync().ConfigureAwait(false);
            if (!restored.IsSuccess) throw new InvalidOperationException(restored.Error!.Message);
            await owner._operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_ended) return;
                _ended = true;
                if (ReferenceEquals(owner._session, this)) owner._session = null;
            }
            finally { owner._operations.Release(); }
        }
    }
}
