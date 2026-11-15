using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Contracts;
using Mosaic.Kernel;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Core;

namespace Mosaic.Desktop;

internal sealed class HostSettingSource(AppPaths paths, HostSettingsOverrides startupValues) : IHostSettingSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, JsonElement> _runtimeOverrides = new(
        startupValues.Values ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
        StringComparer.Ordinal);
    private Dictionary<string, JsonElement>? _rollbackOverrides;
    private string BackupPath => paths.ConfigPath + ".bak";
    private static readonly IReadOnlyList<SettingDefinition> HostDefinitions = SettingCatalog.ForHost()
        .Select(definition => definition.Id switch
        {
            "host.modules" => definition with { DefaultValue = BuiltInModuleCatalog.DefaultModules.ToArray() },
            "host.providers" => definition with { DefaultValue = new Dictionary<string, string>(BuiltInModuleCatalog.DefaultProviders, StringComparer.Ordinal) },
            _ => definition
        })
        .ToArray();

    public string ModuleId => "host";
    public IReadOnlyList<SettingDefinition> Definitions => HostDefinitions;

    public object? GetValue(string id)
    {
        var definition = Definitions.FirstOrDefault(item => item.Id == id);
        if (_runtimeOverrides.TryGetValue(id, out var runtimeValue))
        {
            if (runtimeValue.ValueKind == JsonValueKind.Null) return null;
            return Normalize(definition, JsonNodeToObject(JsonNode.Parse(runtimeValue.GetRawText())!));
        }
        var node = ReadJson()["settings"]?[id];
        return node is null ? null : Normalize(definition, JsonNodeToObject(node));
    }

    private static object? Normalize(SettingDefinition? definition, object? value) =>
        definition is null ? value : SettingValueNormalizer.Normalize(definition, value);

    public Task<OperationResult> ValidateAsync(
        IReadOnlyDictionary<string, object?> effectiveValues,
        CancellationToken cancellationToken = default)
    {
        var modules = effectiveValues.TryGetValue("host.modules", out var moduleValue) && moduleValue is string[] items
            ? items
            : BuiltInModuleCatalog.DefaultModules;
        var providers = effectiveValues.TryGetValue("host.providers", out var providerValue) && providerValue is IReadOnlyDictionary<string, string> map
            ? map
            : BuiltInModuleCatalog.DefaultProviders;
        if (modules.Length == 0)
            return Task.FromResult(OperationResult.Failure(new(ErrorCodes.ValidationFailed, "至少需要启用一个模块。", "host.modules")));
        var unknown = modules.Where(id => !BuiltInModuleCatalog.Definitions.ContainsKey(id)).ToArray();
        if (unknown.Length > 0)
            return Task.FromResult(OperationResult.Failure(new(
                ErrorCodes.ValidationFailed, "未知模块：" + string.Join("、", unknown), "host.modules")));
        var stale = providers.Where(item =>
                !BuiltInModuleCatalog.Definitions.TryGetValue(item.Value, out var definition) ||
                !modules.Contains(item.Value, StringComparer.Ordinal) ||
                !definition.Provides.Any(capability => capability.ToString() == item.Key))
            .Select(item => item.Key)
            .ToArray();
        if (stale.Length > 0)
            return Task.FromResult(OperationResult.Failure(new(
                ErrorCodes.ValidationFailed, "能力绑定无效：" + string.Join("、", stale), "host.providers")));
        try
        {
            _ = ModuleGraph.Resolve(
                modules.Select(id => BuiltInModuleCatalog.Definitions[id]).ToArray(),
                providers);
        }
        catch (InvalidOperationException exception)
        {
            return Task.FromResult(OperationResult.Failure(new(
                ErrorCodes.ValidationFailed, "模块依赖或能力绑定无效：" + exception.Message, "host.modules")));
        }
        return Task.FromResult(OperationResult.Success());
    }

    public async Task<OperationResult> PersistAsync(
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = ReadJson();
            var settings = root["settings"] as JsonObject ?? [];
            foreach (var (id, value) in values)
            {
                settings[id] = value is null ? null : JsonSerializer.SerializeToNode(value, JsonOptions);
            }
            root["settings"] = settings;

            var directory = Path.GetDirectoryName(paths.ConfigPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = paths.ConfigPath + ".tmp";
            var previousOverrides = new Dictionary<string, JsonElement>(_runtimeOverrides, StringComparer.Ordinal);
            try
            {
                await File.WriteAllTextAsync(temporaryPath, root.ToJsonString(JsonOptions), cancellationToken).ConfigureAwait(false);
                if (File.Exists(paths.ConfigPath)) File.Replace(temporaryPath, paths.ConfigPath, BackupPath);
                else File.Move(temporaryPath, paths.ConfigPath);
                foreach (var (id, value) in values)
                {
                    using var document = JsonDocument.Parse(value is null ? "null" : JsonSerializer.Serialize(value, JsonOptions));
                    _runtimeOverrides[id] = document.RootElement.Clone();
                }
                _rollbackOverrides = previousOverrides;
            }
            catch (OperationCanceledException)
            {
                TryDeleteTemporary(temporaryPath);
                throw;
            }
            catch (Exception exception)
            {
                TryDeleteTemporary(temporaryPath);
                return OperationResult.Failure(new(ErrorCodes.StorageUnavailable, "宿主配置保存失败：" + exception.Message));
            }
            return OperationResult.Success();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<OperationResult> RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(BackupPath)) return OperationResult.Success();
            File.Replace(BackupPath, paths.ConfigPath, null);
            if (_rollbackOverrides is not null)
            {
                _runtimeOverrides.Clear();
                foreach (var (id, value) in _rollbackOverrides) _runtimeOverrides[id] = value;
                _rollbackOverrides = null;
            }
            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            return OperationResult.Failure(new(ErrorCodes.StorageUnavailable, "宿主配置回滚失败：" + exception.Message));
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void TryDeleteTemporary(string temporaryPath)
    {
        try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        catch { /* A leftover temporary file must not mask the original failure. */ }
    }

    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private JsonObject ReadJson()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(paths.ConfigPath));
        return JsonSerializer.Deserialize<JsonObject>(document.RootElement.GetRawText(), JsonOptions)
            ?? throw new InvalidOperationException("Host configuration root is not an object.");
    }

    private static object? JsonNodeToObject(JsonNode node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean)) return boolean;
            if (value.TryGetValue<int>(out var integer)) return integer;
            if (value.TryGetValue<long>(out var longValue)) return longValue;
            if (value.TryGetValue<double>(out var doubleValue)) return doubleValue;
            if (value.TryGetValue<string>(out var text)) return text;
        }

        if (node is JsonArray array)
        {
            return array.Select(item => item is null ? null : JsonNodeToObject(item)).ToArray();
        }

        if (node is JsonObject @object)
        {
            return @object.ToDictionary(
                item => item.Key,
                item => item.Value is null ? null : JsonNodeToObject(item.Value),
                StringComparer.Ordinal);
        }

        return node.ToJsonString();
    }
}
