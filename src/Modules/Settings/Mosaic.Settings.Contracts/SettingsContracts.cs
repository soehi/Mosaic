using Mosaic.Contracts;

namespace Mosaic.Settings.Contracts;

public enum SettingValueKind { Boolean, Integer, Double, Enum, String, ModuleList, ProviderMap }
public enum SettingCategory { Normal, Advanced }
public enum SettingApplyMode { Immediate, Restart }
public enum SettingPersistence { UserSettings, HostConfig }
public enum SettingsGroup { Tasks, Appearance, StorageSystem, Text }

public sealed record SettingDefinition(
    string Id,
    string ModuleId,
    string DisplayName,
    string Description,
    SettingValueKind Kind,
    object? DefaultValue,
    object? Min,
    object? Max,
    IReadOnlyList<string>? AllowedValues,
    SettingsGroup Group,
    SettingCategory Category,
    SettingApplyMode ApplyMode,
    SettingPersistence Persistence,
    string? Warning = null);

public sealed record SettingOption(string Value, string DisplayNameKey);

public interface ISettingSource
{
    string ModuleId { get; }
    IReadOnlyList<SettingDefinition> Definitions { get; }
    Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);
}

public interface IHostSettingSource : ISettingSource
{
    object? GetValue(string id);
    Task<OperationResult> PersistAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);
}

public interface ISettingsApi
{
    Task<OperationResult<IReadOnlyList<SettingDefinition>>> ListDefinitionsAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyDictionary<string, object?>>> GetValuesAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
        IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default);
    Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken cancellationToken = default);
}

public interface ISettingsCore : ISettingsApi
{
    IDisposable Register(ISettingSource source);
}

public interface ISettingRepository
{
    Task<OperationResult<IReadOnlyDictionary<string, object?>>> LoadAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> SaveAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);
}
