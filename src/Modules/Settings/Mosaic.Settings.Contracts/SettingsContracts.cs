using Mosaic.Contracts;
using System.Globalization;

namespace Mosaic.Settings.Contracts;

public enum SettingValueKind { Boolean, Integer, Double, Enum, String, ModuleList, ProviderMap }
public enum SettingCategory { Normal, Advanced }
public enum SettingsGroup { Tasks, Appearance, StorageSystem, Text, DesktopPresence }
public enum SettingApplyMode { Immediate, Restart }
public enum SettingPersistence { UserSettings, HostConfig }
public enum SettingPreviewMode { None, Reversible }

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
    string? Warning = null,
    SettingPreviewMode PreviewMode = SettingPreviewMode.None,
    string? DisplayNameKey = null,
    string? DescriptionKey = null,
    IReadOnlyList<SettingOption>? Options = null);

public sealed record SettingPreviewResult(long Revision,
    IReadOnlyDictionary<string, OperationError> Errors,
    IReadOnlyDictionary<string, OperationError> Warnings,
    IReadOnlyList<string> AppliedDefinitionIds,
    IReadOnlyList<string> SaveOnlyDefinitionIds);
public sealed record SettingsCommitResult(IReadOnlyDictionary<string, OperationError> ApplyWarnings);
public sealed record SettingsCommitJournalEntry(
    string CommitId,
    IReadOnlyDictionary<string, object?> HostValues,
    IReadOnlyDictionary<string, object?> UserValues);
public interface ISettingsCommitJournal
{
    Task<OperationResult<SettingsCommitJournalEntry?>> ReadAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> PrepareAsync(SettingsCommitJournalEntry entry, CancellationToken cancellationToken = default);
    Task<OperationResult> CompleteAsync(CancellationToken cancellationToken = default);
}
public interface ISettingsEditSession : IAsyncDisposable
{
    IReadOnlyDictionary<string, object?> SavedValues { get; }
    Task<OperationResult<SettingPreviewResult>> PreviewAsync(IReadOnlyDictionary<string, object?> cumulativeChanges,
        long revision, CancellationToken cancellationToken = default);
    Task<OperationResult<SettingsCommitResult>> CommitAsync(long expectedRevision, CancellationToken cancellationToken = default);
    Task<OperationResult> DiscardAsync(CancellationToken cancellationToken = default);
}

public sealed record SettingOption(string Value, string DisplayNameKey);

public static class SettingResults
{
    public const string ApplyWarningId = "settings.apply";
    public const string CommitWarningId = "settings.commit";
}

public enum SettingFormatError
{
    None,
    Integer,
    ProviderMap,
    Unsupported
}

public sealed record CapabilityBinding(string Capability, int Version, string ModuleId)
{
    public override string ToString() => $"{Capability}@{Version}={ModuleId}";

    public static bool TryParse(string text, out CapabilityBinding? binding)
    {
        binding = null;
        var equals = text.IndexOf('=');
        if (equals <= 0 || equals == text.Length - 1) return false;
        var capabilityText = text[..equals].Trim();
        var moduleId = text[(equals + 1)..].Trim();
        var at = capabilityText.LastIndexOf('@');
        if (at <= 0 || at == capabilityText.Length - 1 ||
            !int.TryParse(capabilityText[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version < 1 || !SettingValueFormat.IsCapabilityId(capabilityText[..at]) ||
            !SettingValueFormat.IsModuleId(moduleId))
            return false;
        binding = new(capabilityText[..at], version, moduleId);
        return true;
    }
}

public static class SettingValueFormat
{
    public const string ProviderMapExample = "settings.api@1=settings-core";
    public const string ModuleListExample = "tasks-core, tasks-ui";

    public static string Example(SettingValueKind kind) => kind switch
    {
        SettingValueKind.ModuleList => ModuleListExample,
        SettingValueKind.ProviderMap => ProviderMapExample,
        _ => string.Empty
    };

    public static bool TryParse(SettingValueKind kind, string? text, out object? value, out SettingFormatError error)
    {
        value = null;
        error = SettingFormatError.None;
        switch (kind)
        {
            case SettingValueKind.Integer:
                if (!long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    error = SettingFormatError.Integer;
                    return false;
                }
                value = integer;
                return true;
            case SettingValueKind.ModuleList:
                value = SplitModuleList(text);
                return true;
            case SettingValueKind.ProviderMap:
                if (!TryParseProviderMap(text, out var providers))
                {
                    error = SettingFormatError.ProviderMap;
                    return false;
                }
                value = providers;
                return true;
            case SettingValueKind.String:
                value = text ?? string.Empty;
                return true;
            default:
                error = SettingFormatError.Unsupported;
                return false;
        }
    }

    public static string Format(SettingValueKind kind, object? value) => value switch
    {
        null => string.Empty,
        IReadOnlyDictionary<string, string> providers when kind == SettingValueKind.ProviderMap =>
            string.Join("; ", providers.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}={item.Value}")),
        IReadOnlyDictionary<string, object?> providers when kind == SettingValueKind.ProviderMap =>
            string.Join("; ", providers.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}={item.Value}")),
        IEnumerable<string> values when kind == SettingValueKind.ModuleList => string.Join(", ", values),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    public static bool TryParseProviderMap(string? text, out Dictionary<string, string> providers)
    {
        providers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in (text ?? string.Empty).Split([';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!CapabilityBinding.TryParse(part, out var binding) ||
                !providers.TryAdd($"{binding!.Capability}@{binding.Version}", binding.ModuleId))
                return false;
        }
        return true;
    }

    public static string[] SplitModuleList(string? text) =>
        (text ?? string.Empty).Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static bool IsModuleId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 96 &&
        char.IsAsciiLetterLower(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    public static bool IsCapabilityId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160) return false;
        var segmentStart = 0;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index < value.Length && value[index] != '.') continue;
            if (index == segmentStart || !char.IsAsciiLetterLower(value[segmentStart])) return false;
            for (var segmentIndex = segmentStart + 1; segmentIndex < index; segmentIndex++)
                if (!char.IsAsciiLetterOrDigit(value[segmentIndex]) && value[segmentIndex] != '-') return false;
            segmentStart = index + 1;
        }
        return true;
    }

    public static bool IsValidModuleList(IReadOnlyList<string> modules) =>
        modules.Count <= 64 &&
        modules.All(IsModuleId) &&
        modules.Distinct(StringComparer.Ordinal).Count() == modules.Count;

    public static bool IsValidProviderMap(IReadOnlyDictionary<string, string> providers) =>
        providers.Count <= 32 &&
        providers.All(item => CapabilityBinding.TryParse($"{item.Key}={item.Value}", out _));
}

public static class SettingValueComparer
{
    public static bool Equivalent(SettingDefinition definition, object? left, object? right)
    {
        var normalizedLeft = SettingValueNormalizer.Normalize(definition, left);
        var normalizedRight = SettingValueNormalizer.Normalize(definition, right);
        return definition.Kind switch
        {
            SettingValueKind.ModuleList when normalizedLeft is string[] leftItems && normalizedRight is string[] rightItems =>
                leftItems.SequenceEqual(rightItems, StringComparer.Ordinal),
            SettingValueKind.ProviderMap when normalizedLeft is IReadOnlyDictionary<string, string> leftMap &&
                                                normalizedRight is IReadOnlyDictionary<string, string> rightMap =>
                leftMap.Count == rightMap.Count &&
                leftMap.All(item => rightMap.TryGetValue(item.Key, out var value) && string.Equals(item.Value, value, StringComparison.Ordinal)),
            SettingValueKind.Integer => Convert.ToInt64(normalizedLeft, CultureInfo.InvariantCulture) ==
                                        Convert.ToInt64(normalizedRight, CultureInfo.InvariantCulture),
            SettingValueKind.Double => Math.Abs(Convert.ToDouble(normalizedLeft, CultureInfo.InvariantCulture) -
                                                Convert.ToDouble(normalizedRight, CultureInfo.InvariantCulture)) <= double.Epsilon,
            _ => Equals(normalizedLeft, normalizedRight)
        };
    }
}

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
    Task<OperationResult> ValidateAsync(IReadOnlyDictionary<string, object?> effectiveValues, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());
    /// <summary>Awaited, idempotent application of a complete immediate runtime snapshot. Cache for later view attachment.</summary>
    Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);
}

public interface IHostSettingSource : ISettingSource
{
    ISettingsCommitJournal? CommitJournal => null;
    object? GetValue(string id);
    new Task<OperationResult> ValidateAsync(IReadOnlyDictionary<string, object?> effectiveValues, CancellationToken cancellationToken = default);
    Task<OperationResult> PersistAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);
    Task<OperationResult> RollbackAsync(CancellationToken cancellationToken = default);
}

public interface ISettingsApi
{
    Task<OperationResult<ISettingsEditSession>> BeginEditSessionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<ISettingsEditSession>.Failure(new(ErrorCodes.CapabilityUnavailable, "设置编辑会话不可用。")));
    Task<OperationResult<IReadOnlyList<SettingDefinition>>> ListDefinitionsAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyDictionary<string, object?>>> GetValuesAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
        IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default);
    Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken cancellationToken = default);
    Task<OperationResult> ResetAsync(IReadOnlyList<string> definitionIds, CancellationToken cancellationToken = default);
    async Task<OperationResult<SettingsCommitResult>> ResetWithResultAsync(
        string? moduleId = null, CancellationToken cancellationToken = default)
    {
        var result = await ResetAsync(moduleId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? OperationResult<SettingsCommitResult>.Success(new(
                new Dictionary<string, OperationError>(StringComparer.Ordinal)))
            : OperationResult<SettingsCommitResult>.Failure(result.Error!);
    }
    async Task<OperationResult<SettingsCommitResult>> ResetWithResultAsync(
        IReadOnlyList<string> definitionIds, CancellationToken cancellationToken = default)
    {
        var result = await ResetAsync(definitionIds, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? OperationResult<SettingsCommitResult>.Success(new(
                new Dictionary<string, OperationError>(StringComparer.Ordinal)))
            : OperationResult<SettingsCommitResult>.Failure(result.Error!);
    }
}

public interface ISettingsCore : ISettingsApi
{
    IDisposable Register(ISettingSource source);
    Task<OperationResult> PrepareSourceStopAsync(string moduleId, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
}

public interface ISettingRepository
{
    Task<OperationResult<IReadOnlyDictionary<string, object?>>> LoadAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> SaveAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);
}
