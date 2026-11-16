using Mosaic.Contracts;
using Mosaic.Settings.Contracts;

namespace Mosaic.Settings.Core.Internal;

internal sealed partial class SettingsService(ISettingRepository repository, IHostSettingSource? hostSource) : ISettingsCore
{
    private readonly List<ISettingSource> _sources = [];
    private readonly object _gate = new();

    public IDisposable Register(ISettingSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            if (_sources.Any(item => item.ModuleId == source.ModuleId))
                throw new InvalidOperationException("A setting source is already registered for this module.");
            var allDefinitions = _sources.SelectMany(item => item.Definitions).ToArray();
            var duplicateIds = source.Definitions
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .Concat(source.Definitions.Where(item => allDefinitions.Any(existing => existing.Id == item.Id)).Select(item => item.Id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (duplicateIds.Length > 0)
                throw new InvalidOperationException("A setting definition is already registered: " + string.Join(", ", duplicateIds));
            var invalidModules = source.Definitions.Where(item => !string.Equals(item.ModuleId, source.ModuleId, StringComparison.Ordinal)).Select(item => item.Id).ToArray();
            if (invalidModules.Length > 0)
                throw new InvalidOperationException("A setting definition belongs to another module: " + string.Join(", ", invalidModules));
            _sources.Add(source);
        }
        return new SourceRegistration(this, source);
    }

    private IReadOnlyList<SettingDefinition> Definitions
    {
        get
        {
            lock (_gate)
            {
                return [.. hostSource?.Definitions ?? [], .. _sources.Where(item => !_preparedSources.Contains(item.ModuleId)).SelectMany(item => item.Definitions)];
            }
        }
    }

    public Task<OperationResult<IReadOnlyList<SettingDefinition>>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<IReadOnlyList<SettingDefinition>>.Success(Definitions));

    public async Task<OperationResult<IReadOnlyDictionary<string, object?>>> GetValuesAsync(CancellationToken cancellationToken = default)
    {
        var stored = await repository.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess) return OperationResult<IReadOnlyDictionary<string, object?>>.Failure(stored.Error!);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var definition in Definitions)
        {
            if (definition.Persistence == SettingPersistence.HostConfig && hostSource?.GetValue(definition.Id) is { } hostValue)
                values[definition.Id] = SettingValueNormalizer.Normalize(definition, hostValue);
            else if (stored.Value.TryGetValue(definition.Id, out var value))
                values[definition.Id] = SettingValueNormalizer.Normalize(definition, value);
            else
                values[definition.Id] = SettingValueNormalizer.Normalize(definition, definition.DefaultValue);
        }
        return OperationResult<IReadOnlyDictionary<string, object?>>.Success(values);
    }

    internal async Task<OperationResult> RecoverPendingCommitAsync(CancellationToken cancellationToken = default)
    {
        var journal = hostSource?.CommitJournal;
        if (journal is null) return OperationResult.Success();
        var pending = await journal.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!pending.IsSuccess) return OperationResult.Failure(pending.Error!);
        if (pending.Value is not { } entry) return OperationResult.Success();
        if (entry.HostValues.Count > 0 && hostSource is not null)
        {
            var host = await hostSource.PersistAsync(entry.HostValues, cancellationToken).ConfigureAwait(false);
            if (!host.IsSuccess) return OperationResult.Failure(host.Error!);
        }
        if (entry.UserValues.Count > 0)
        {
            var user = await repository.SaveAsync(entry.UserValues, cancellationToken).ConfigureAwait(false);
            if (!user.IsSuccess) return OperationResult.Failure(user.Error!);
        }
        var completed = await journal.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        return completed;
    }

    private async Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesCoreAsync(
        IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, OperationError>(StringComparer.Ordinal);
        var normalizedChanges = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (id, value) in changes)
        {
            var definition = Definitions.FirstOrDefault(item => item.Id == id);
            if (definition is null) { errors[id] = new(ErrorCodes.InvalidRequest, "未知设置项。", id); continue; }
            var normalized = SettingValueNormalizer.Normalize(definition, value);
            normalizedChanges[id] = normalized;
            if (Validate(definition, normalized) is { } error) errors[id] = error;
        }
        if (errors.Count > 0) return OperationResult<IReadOnlyDictionary<string, OperationError>>.Success(errors);

        var baseline = await GetValuesAsync(cancellationToken).ConfigureAwait(false);
        if (!baseline.IsSuccess) return OperationResult<IReadOnlyDictionary<string, OperationError>>.Failure(baseline.Error!);
        var candidate = new Dictionary<string, object?>(baseline.Value, StringComparer.Ordinal);
        foreach (var (id, value) in normalizedChanges) candidate[id] = value;
        ISettingSource[] sources;
        lock (_gate) sources = _sources.ToArray();
        foreach (var source in sources)
        {
            var validation = await source.ValidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (!validation.IsSuccess)
                errors[validation.Error!.Field ?? source.ModuleId] = validation.Error;
        }
        if (errors.Count > 0) return OperationResult<IReadOnlyDictionary<string, OperationError>>.Success(errors);

        var userChanges = normalizedChanges.Where(item => Find(item.Key)!.Persistence == SettingPersistence.UserSettings)
            .ToDictionary(item => item.Key, item => item.Value);
        var hostChanges = normalizedChanges.Where(item => Find(item.Key)!.Persistence == SettingPersistence.HostConfig)
            .ToDictionary(item => item.Key, item => item.Value);
        if (hostSource is not null && hostChanges.Count > 0)
        {
            var current = await GetValuesAsync(cancellationToken).ConfigureAwait(false);
            if (!current.IsSuccess) return OperationResult<IReadOnlyDictionary<string, OperationError>>.Failure(current.Error!);
            var effective = new Dictionary<string, object?>(current.Value, StringComparer.Ordinal);
            foreach (var (id, value) in hostChanges) effective[id] = value;
            var semantic = await hostSource.ValidateAsync(effective, cancellationToken).ConfigureAwait(false);
            if (!semantic.IsSuccess)
                return OperationResult<IReadOnlyDictionary<string, OperationError>>.Success(
                    new Dictionary<string, OperationError>(StringComparer.Ordinal)
                    {
                        [semantic.Error!.Field ?? "host.modules"] = semantic.Error
                    });
        }
        var hostPersisted = false;
        var journal = hostSource?.CommitJournal;
        var journalPrepared = false;
        if (journal is not null && hostChanges.Count > 0 && userChanges.Count > 0)
        {
            var prepared = await journal.PrepareAsync(
                new("settings-" + Guid.NewGuid().ToString("N"), hostChanges, userChanges),
                cancellationToken).ConfigureAwait(false);
            if (!prepared.IsSuccess)
                return OperationResult<IReadOnlyDictionary<string, OperationError>>.Failure(prepared.Error!);
            journalPrepared = true;
        }
        if (hostSource is not null && hostChanges.Count > 0)
        {
            var persisted = await hostSource.PersistAsync(hostChanges, cancellationToken).ConfigureAwait(false);
            if (!persisted.IsSuccess)
            {
                await CompleteJournalAsync(journal, journalPrepared).ConfigureAwait(false);
                return OperationResult<IReadOnlyDictionary<string, OperationError>>.Failure(persisted.Error!);
            }
            hostPersisted = true;
        }
        try
        {
            if (userChanges.Count > 0)
            {
                var saved = await repository.SaveAsync(userChanges, cancellationToken).ConfigureAwait(false);
                if (!saved.IsSuccess)
                {
                    if (hostPersisted)
                    {
                        var rollback = await hostSource!.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        if (!rollback.IsSuccess) return OperationResult<IReadOnlyDictionary<string, OperationError>>.Failure(rollback.Error!);
                        await CompleteJournalAsync(journal, journalPrepared).ConfigureAwait(false);
                    }
                    return OperationResult<IReadOnlyDictionary<string, OperationError>>.Failure(saved.Error!);
                }
            }
        }
        catch (Exception)
        {
            if (hostPersisted)
            {
                try
                {
                    var rollback = await hostSource!.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    if (!rollback.IsSuccess) return OperationResult<IReadOnlyDictionary<string, OperationError>>.Failure(rollback.Error!);
                    await CompleteJournalAsync(journal, journalPrepared).ConfigureAwait(false);
                }
                catch { return OperationResult<IReadOnlyDictionary<string, OperationError>>.Failure(new(ErrorCodes.InternalError, "宿主配置补偿失败，运行状态未确认。")); }
            }
            return OperationResult<IReadOnlyDictionary<string, OperationError>>.Failure(new(ErrorCodes.StorageUnavailable, "设置未保存，请重试。"));
        }
        if (journalPrepared)
        {
            var completed = await journal!.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            if (!completed.IsSuccess)
                errors[SettingResults.CommitWarningId] = new(
                    ErrorCodes.StorageUnavailable,
                    "设置已保存，但提交日志未清理；下次启动将校验并重放。",
                    MessageKey: "Settings.Warning.JournalCleanup");
        }
        if (normalizedChanges.Keys.Any(id => Find(id)!.ApplyMode == SettingApplyMode.Immediate))
        {
            var warnings = await TryApplyAsync(ImmediateValues(candidate),
                Definitions.Select(item => item.ModuleId).Distinct(StringComparer.Ordinal), CancellationToken.None).ConfigureAwait(false);
            foreach (var (id, warning) in warnings) errors[id] = warning;
        }
        return OperationResult<IReadOnlyDictionary<string, OperationError>>.Success(errors);
    }

    private static async Task CompleteJournalAsync(ISettingsCommitJournal? journal, bool prepared)
    {
        if (journal is null || !prepared) return;
        try { await journal.CompleteAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }

    private async Task<Dictionary<string, OperationError>> TryApplyAsync(
        IReadOnlyDictionary<string, object?> values,
        IEnumerable<string> moduleIds,
        CancellationToken cancellationToken)
    {
        var warnings = new Dictionary<string, OperationError>(StringComparer.Ordinal);
        foreach (var moduleId in moduleIds)
        {
            var source = Sources.FirstOrDefault(item => item.ModuleId == moduleId);
            if (source is null) continue;
            try
            {
                await source.ApplyAsync(values, cancellationToken).ConfigureAwait(false);
                foreach (var definition in source.Definitions.Where(item => item.ApplyMode == SettingApplyMode.Immediate))
                    if (values.TryGetValue(definition.Id, out var value)) _running[definition.Id] = value;
            }
            catch (Exception)
            {
                warnings[SettingResults.ApplyWarningId] = new(
                    ErrorCodes.InternalError, "已保存，但即时应用失败，重启后生效。");
            }
        }
        return warnings;
    }

    private static OperationError? Validate(SettingDefinition definition, object? value)
    {
        if (value is null && definition.Kind is not SettingValueKind.String and not SettingValueKind.ModuleList and not SettingValueKind.ProviderMap)
            return new(ErrorCodes.ValidationFailed, "设置值不能为空。", definition.Id);
        return definition.Kind switch
        {
            SettingValueKind.Boolean when value is not bool => new(ErrorCodes.ValidationFailed, "设置值必须是布尔类型。", definition.Id),
            SettingValueKind.Integer when value is not int && value is not long => new(ErrorCodes.ValidationFailed, "设置值必须是整数。", definition.Id),
            SettingValueKind.Double when value is not double && value is not int && value is not long => new(ErrorCodes.ValidationFailed, "设置值必须是数字。", definition.Id),
            SettingValueKind.Double when value is not null && (!double.IsFinite(Convert.ToDouble(value)) ||
                definition.Min is not null && Convert.ToDouble(value) < Convert.ToDouble(definition.Min) ||
                definition.Max is not null && Convert.ToDouble(value) > Convert.ToDouble(definition.Max)) => new(ErrorCodes.ValidationFailed, "数字必须是范围内的有限值。", definition.Id),
            SettingValueKind.String when value is not null and not string => new(ErrorCodes.ValidationFailed, "设置值必须是文本。", definition.Id),
            SettingValueKind.Enum when value is not string || definition.AllowedValues?.Contains((string)value, StringComparer.Ordinal) != true => new(ErrorCodes.ValidationFailed, "设置值不在允许范围内。", definition.Id),
            SettingValueKind.String when value is string text && text.EnumerateRunes().Count() > 200 => new(ErrorCodes.ValidationFailed, "文字最多 200 个字符。", definition.Id),
            SettingValueKind.String when definition.Id == "storage.filename" && value is string filename && !IsValidDatabaseFilename(filename) =>
                new(ErrorCodes.ValidationFailed, "数据库名称必须是合法的单个文件名。", definition.Id),
            SettingValueKind.String when definition.Id == "storage.location" && value is string location && !IsValidStorageLocation(location) =>
                new(ErrorCodes.ValidationFailed, "存储位置必须是 local-app-data 或绝对路径。", definition.Id),
            SettingValueKind.ModuleList when value is not string[] modules || !SettingValueFormat.IsValidModuleList(modules) =>
                new(ErrorCodes.ValidationFailed, "模块列表格式无效：应为逗号分隔的模块 ID，且不能重复。", definition.Id),
            SettingValueKind.ProviderMap when value is not IReadOnlyDictionary<string, string> providers || !SettingValueFormat.IsValidProviderMap(providers) =>
                new(ErrorCodes.ValidationFailed, "能力绑定格式无效：应为 capability@version=module-id。", definition.Id),
            SettingValueKind.Integer when value is int integer && !InRange(definition, integer) => new(ErrorCodes.ValidationFailed, $"设置值必须在 {definition.Min} 到 {definition.Max} 之间。", definition.Id),
            SettingValueKind.Integer when value is long longValue && !InRange(definition, longValue) => new(ErrorCodes.ValidationFailed, $"设置值必须在 {definition.Min} 到 {definition.Max} 之间。", definition.Id),
            _ => null
        };
    }

    private static bool IsValidDatabaseFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename is "." or "..") return false;
        if (filename != Path.GetFileName(filename)) return false;
        if (filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (filename.EndsWith('.') || filename.EndsWith(' ')) return false;
        return !IsReservedDatabaseName(filename);
    }

    private static bool IsValidStorageLocation(string location) =>
        location == "local-app-data" ||
        (Path.IsPathFullyQualified(location) && location.IndexOfAny(Path.GetInvalidPathChars()) < 0);

    private static bool IsReservedDatabaseName(string filename)
    {
        var name = filename.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9');
    }

    private static bool InRange(SettingDefinition definition, long value) =>
        (definition.Min is int minimum ? minimum : long.MinValue) <= value &&
        value <= (definition.Max is int maximum ? maximum : long.MaxValue);

    private SettingDefinition? Find(string id) => Definitions.FirstOrDefault(item => item.Id == id);

    private Task<OperationResult<SettingsCommitResult>> ResetCoreAsync(string? moduleId = null, CancellationToken cancellationToken = default) =>
        ResetDefinitionsAsync(
            Definitions.Where(item => moduleId is null || item.ModuleId == moduleId).ToArray(),
            cancellationToken);

    private Task<OperationResult<SettingsCommitResult>> ResetCoreAsync(IReadOnlyList<string> definitionIds, CancellationToken cancellationToken = default)
    {
        var definitions = new List<SettingDefinition>();
        var known = Definitions;
        foreach (var id in definitionIds.Distinct(StringComparer.Ordinal))
        {
            var definition = known.FirstOrDefault(item => item.Id == id);
            if (definition is null)
                return Task.FromResult(OperationResult<SettingsCommitResult>.Failure(new(ErrorCodes.InvalidRequest, "未知设置项：" + id, id)));
            definitions.Add(definition);
        }
        return ResetDefinitionsAsync(definitions, cancellationToken);
    }

    private async Task<OperationResult<SettingsCommitResult>> ResetDefinitionsAsync(
        IReadOnlyList<SettingDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var ids = definitions.Where(item => item.Persistence == SettingPersistence.UserSettings).Select(item => item.Id).ToArray();
        var userDefaults = definitions.Where(item => item.Persistence == SettingPersistence.UserSettings)
            .ToDictionary(item => item.Id, item => item.DefaultValue, StringComparer.Ordinal);
        var hostPersisted = false;
        var hostIds = definitions.Where(item => item.Persistence == SettingPersistence.HostConfig).Select(item => item.Id).ToArray();
        var defaults = definitions.Where(item => item.Persistence == SettingPersistence.HostConfig)
            .ToDictionary(item => item.Id, item => item.DefaultValue, StringComparer.Ordinal);
        var journal = hostSource?.CommitJournal;
        var journalPrepared = false;
        if (journal is not null && defaults.Count > 0 && userDefaults.Count > 0)
        {
            var prepared = await journal.PrepareAsync(
                new("settings-reset-" + Guid.NewGuid().ToString("N"), defaults, userDefaults),
                cancellationToken).ConfigureAwait(false);
            if (!prepared.IsSuccess) return OperationResult<SettingsCommitResult>.Failure(prepared.Error!);
            journalPrepared = true;
        }
        if (hostIds.Length > 0 && hostSource is not null)
        {
            var current = await GetValuesAsync(cancellationToken).ConfigureAwait(false);
            if (!current.IsSuccess)
            {
                await CompleteJournalAsync(journal, journalPrepared).ConfigureAwait(false);
                return OperationResult<SettingsCommitResult>.Failure(current.Error!);
            }
            var effective = new Dictionary<string, object?>(current.Value, StringComparer.Ordinal);
            foreach (var (id, value) in defaults) effective[id] = value;
            var semantic = await hostSource.ValidateAsync(effective, cancellationToken).ConfigureAwait(false);
            if (!semantic.IsSuccess)
            {
                await CompleteJournalAsync(journal, journalPrepared).ConfigureAwait(false);
                return OperationResult<SettingsCommitResult>.Failure(semantic.Error!);
            }
            var persisted = await hostSource.PersistAsync(defaults, cancellationToken).ConfigureAwait(false);
            if (!persisted.IsSuccess)
            {
                await CompleteJournalAsync(journal, journalPrepared).ConfigureAwait(false);
                return OperationResult<SettingsCommitResult>.Failure(persisted.Error!);
            }
            hostPersisted = true;
        }
        if (ids.Length > 0)
        {
            var deleted = await repository.DeleteAsync(ids, cancellationToken).ConfigureAwait(false);
            if (!deleted.IsSuccess)
            {
                if (hostPersisted)
                {
                    var rollback = await hostSource!.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    if (!rollback.IsSuccess) return OperationResult<SettingsCommitResult>.Failure(rollback.Error!);
                    await CompleteJournalAsync(journal, journalPrepared).ConfigureAwait(false);
                }
                return OperationResult<SettingsCommitResult>.Failure(new(ErrorCodes.StorageUnavailable, "恢复默认失败。"));
            }
        }
        if (journalPrepared)
            await CompleteJournalAsync(journal, journalPrepared).ConfigureAwait(false);
        var values = await GetValuesAsync(cancellationToken).ConfigureAwait(false);
        if (!values.IsSuccess) return OperationResult<SettingsCommitResult>.Failure(values.Error!);
        var warnings = await TryApplyAsync(
            values.Value.Where(item => Find(item.Key)?.ApplyMode == SettingApplyMode.Immediate)
                .ToDictionary(item => item.Key, item => item.Value),
            Definitions.Select(item => item.ModuleId).Distinct(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        return OperationResult<SettingsCommitResult>.Success(new(warnings));
    }

    private sealed class SourceRegistration(SettingsService owner, ISettingSource source) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (owner._session is not null && source.Definitions.Any(item => owner._session.SavedValues.ContainsKey(item.Id)))
                    throw new InvalidOperationException("Call PrepareSourceStopAsync before unregistering an active preview source.");
                owner._sources.Remove(source);
                owner._preparedSources.Remove(source.ModuleId);
            }
        }
    }
}
