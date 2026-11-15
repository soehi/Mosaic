using Mosaic.Contracts;
using Mosaic.Settings.Contracts;

namespace Mosaic.Settings.Core.Internal;

internal sealed class SettingsService(ISettingRepository repository, IHostSettingSource? hostSource) : ISettingsCore
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
            var allDefinitions = SettingCatalog.Definitions.Concat(_sources.SelectMany(item => item.Definitions)).ToArray();
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
                return [.. hostSource?.Definitions ?? [], .. _sources.SelectMany(item => item.Definitions)];
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
                values[definition.Id] = hostValue;
            else if (stored.Value.TryGetValue(definition.Id, out var value))
                values[definition.Id] = value;
            else
                values[definition.Id] = definition.DefaultValue;
        }
        return OperationResult<IReadOnlyDictionary<string, object?>>.Success(values);
    }

    public async Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
        IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, OperationError>(StringComparer.Ordinal);
        foreach (var (id, value) in changes)
        {
            var definition = Definitions.FirstOrDefault(item => item.Id == id);
            if (definition is null) { errors[id] = new(ErrorCodes.InvalidRequest, "未知设置项。", id); continue; }
            if (Validate(definition, value) is { } error) errors[id] = error;
        }
        if (errors.Count > 0) return OperationResult<IReadOnlyDictionary<string, OperationError>>.Success(errors);

        var userChanges = changes.Where(item => Find(item.Key)!.Persistence == SettingPersistence.UserSettings)
            .ToDictionary(item => item.Key, item => item.Value);
        if (userChanges.Count > 0 && !(await repository.SaveAsync(userChanges, cancellationToken).ConfigureAwait(false)).IsSuccess)
            return OperationResult<IReadOnlyDictionary<string, OperationError>>.Failure(new(ErrorCodes.StorageUnavailable, "设置保存失败。"));

        if (hostSource is not null)
        {
            var hostChanges = changes.Where(item => Find(item.Key)!.Persistence == SettingPersistence.HostConfig)
                .ToDictionary(item => item.Key, item => item.Value);
            if (hostChanges.Count > 0)
            {
                var persisted = await hostSource.PersistAsync(hostChanges, cancellationToken).ConfigureAwait(false);
                if (!persisted.IsSuccess) return OperationResult<IReadOnlyDictionary<string, OperationError>>.Failure(persisted.Error!);
            }
        }

        var immediate = changes.Where(item => Find(item.Key)!.ApplyMode == SettingApplyMode.Immediate)
            .ToDictionary(item => item.Key, item => item.Value);
        if (immediate.Count > 0)
        {
            var values = (await GetValuesAsync(cancellationToken).ConfigureAwait(false)).Value;
            foreach (var group in immediate.GroupBy(item => Find(item.Key)!.ModuleId))
            {
                var source = _sources.FirstOrDefault(item => item.ModuleId == group.Key);
                if (source is not null) await source.ApplyAsync(values, cancellationToken).ConfigureAwait(false);
            }
        }
        return OperationResult<IReadOnlyDictionary<string, OperationError>>.Success(errors);
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
            SettingValueKind.Enum when value is not string || definition.AllowedValues?.Contains((string)value, StringComparer.Ordinal) != true => new(ErrorCodes.ValidationFailed, "设置值不在允许范围内。", definition.Id),
            SettingValueKind.String when value is string text && text.EnumerateRunes().Count() > 200 => new(ErrorCodes.ValidationFailed, "文字最多 200 个字符。", definition.Id),
            SettingValueKind.Integer when value is int integer && !InRange(definition, integer) => new(ErrorCodes.ValidationFailed, $"设置值必须在 {definition.Min} 到 {definition.Max} 之间。", definition.Id),
            SettingValueKind.Integer when value is long longValue && !InRange(definition, longValue) => new(ErrorCodes.ValidationFailed, $"设置值必须在 {definition.Min} 到 {definition.Max} 之间。", definition.Id),
            _ => null
        };
    }

    private static bool InRange(SettingDefinition definition, long value) =>
        (definition.Min is int minimum ? minimum : long.MinValue) <= value &&
        value <= (definition.Max is int maximum ? maximum : long.MaxValue);

    private SettingDefinition? Find(string id) => Definitions.FirstOrDefault(item => item.Id == id);

    public async Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken cancellationToken = default)
    {
        var definitions = Definitions.Where(item => moduleId is null || item.ModuleId == moduleId).ToArray();
        var ids = definitions.Where(item => item.Persistence == SettingPersistence.UserSettings).Select(item => item.Id).ToArray();
        if (ids.Length > 0 && !(await repository.DeleteAsync(ids, cancellationToken).ConfigureAwait(false)).IsSuccess)
            return OperationResult.Failure(new(ErrorCodes.StorageUnavailable, "恢复默认失败。"));
        var hostIds = definitions.Where(item => item.Persistence == SettingPersistence.HostConfig).Select(item => item.Id).ToArray();
        if (hostIds.Length > 0 && hostSource is not null)
        {
            var defaults = definitions.Where(item => hostIds.Contains(item.Id)).ToDictionary(item => item.Id, item => item.DefaultValue);
            var persisted = await hostSource.PersistAsync(defaults, cancellationToken).ConfigureAwait(false);
            if (!persisted.IsSuccess) return persisted;
        }
        var values = (await GetValuesAsync(cancellationToken).ConfigureAwait(false)).Value;
        foreach (var source in _sources.Where(item => moduleId is null || item.ModuleId == moduleId))
            await source.ApplyAsync(values, cancellationToken).ConfigureAwait(false);
        return OperationResult.Success();
    }

    private sealed class SourceRegistration(SettingsService owner, ISettingSource source) : IDisposable
    {
        public void Dispose() => owner._sources.Remove(source);
    }
}
