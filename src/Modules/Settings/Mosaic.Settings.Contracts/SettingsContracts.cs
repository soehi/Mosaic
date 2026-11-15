using Mosaic.Contracts;
using System.Globalization;

namespace Mosaic.Settings.Contracts;

public enum SettingValueKind { Boolean, Integer, Double, Enum, String, ModuleList, ProviderMap }
public enum SettingCategory { Normal, Advanced }
public enum SettingApplyMode { Immediate, Restart }
public enum SettingPersistence { UserSettings, HostConfig }

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
    SettingCategory Category,
    SettingApplyMode ApplyMode,
    SettingPersistence Persistence,
    string? Warning = null);

public sealed record SettingOption(string Value, string DisplayNameKey);

public static class SettingValueNormalizer
{
    public static object? Normalize(SettingDefinition definition, object? value)
    {
        if (value is null) return null;
        return definition.Kind switch
        {
            SettingValueKind.Integer => ToInt32(value) ?? value,
            SettingValueKind.Double => ToDouble(value) ?? value,
            SettingValueKind.ModuleList => ToModuleList(value) ?? value,
            SettingValueKind.ProviderMap => ToProviderMap(value) ?? value,
            _ => value
        };
    }

    private static int? ToInt32(object value) => value switch
    {
        int integer => integer,
        long integer when integer >= int.MinValue && integer <= int.MaxValue => (int)integer,
        _ => null
    };

    private static double? ToDouble(object value) => value switch
    {
        double number => number,
        int integer => integer,
        long integer => integer,
        _ => null
    };

    private static string[]? ToModuleList(object value) => value switch
    {
        string[] items => items,
        IEnumerable<string> items => items.ToArray(),
        IEnumerable<object?> items => items
            .Select(item => Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty)
            .ToArray(),
        _ => null
    };

    private static Dictionary<string, string>? ToProviderMap(object value) => value switch
    {
        IReadOnlyDictionary<string, string> map => new Dictionary<string, string>(map, StringComparer.Ordinal),
        IReadOnlyDictionary<string, object?> map => map.ToDictionary(
            item => item.Key,
            item => Convert.ToString(item.Value, CultureInfo.InvariantCulture) ?? string.Empty,
            StringComparer.Ordinal),
        _ => null
    };
}

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
