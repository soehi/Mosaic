using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Contracts;
using Mosaic.Kernel;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Core;

namespace Mosaic.Desktop;

internal sealed class HostSettingSource : IHostSettingSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly AppPaths _paths;
    private readonly ISettingsCommitJournal _commitJournal;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, JsonElement> _runtimeOverrides;
    private Dictionary<string, JsonElement>? _rollbackOverrides;
    private string BackupPath => _paths.ConfigPath + ".bak";
    private static readonly IReadOnlyList<SettingDefinition> HostDefinitions = HostSettingsCatalog.Create();

    private readonly IWindowsStartupRegistration _startup;

    internal HostSettingSource(AppPaths paths, AppConfiguration configuration)
        : this(paths, configuration, new WindowsStartupRegistration()) { }

    internal HostSettingSource(AppPaths paths, AppConfiguration configuration, IWindowsStartupRegistration startup)
    {
        _paths = paths;
        _startup = startup;
        _commitJournal = new FileSettingsCommitJournal(paths);
        _runtimeOverrides = BuildEffectiveValues(configuration);
    }

    public string ModuleId => "host";
    public ISettingsCommitJournal CommitJournal => _commitJournal;
    public IReadOnlyList<SettingDefinition> Definitions => HostDefinitions;

    public object? GetValue(string id)
    {
        var definition = Definitions.FirstOrDefault(item => item.Id == id);
        if (_runtimeOverrides.TryGetValue(id, out var runtimeValue))
        {
            if (runtimeValue.ValueKind == JsonValueKind.Null) return null;
            return Normalize(definition, JsonNodeToObject(JsonNode.Parse(runtimeValue.GetRawText())!));
        }
        return null;
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
        foreach (var (sizeId, minimumId) in new[] { ("startup.width", "shell.window.minWidth"), ("startup.height", "shell.window.minHeight") })
        {
            if (effectiveValues.TryGetValue(sizeId, out var size) && effectiveValues.TryGetValue(minimumId, out var minimum)
                && Convert.ToDouble(size) < Convert.ToDouble(minimum))
                return Task.FromResult(OperationResult.Failure(new(ErrorCodes.ValidationFailed, "启动尺寸不得小于窗口最小尺寸。", sizeId)));
        }
        var startup = ValidateStartup(effectiveValues);
        if (!startup.IsSuccess) return Task.FromResult(startup);
        return Task.FromResult(HostConfigurationValidator.Validate(modules, providers));
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

            var directory = Path.GetDirectoryName(_paths.ConfigPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _paths.ConfigPath + ".tmp";
            var previousOverrides = new Dictionary<string, JsonElement>(_runtimeOverrides, StringComparer.Ordinal);
            try
            {
                await File.WriteAllTextAsync(temporaryPath, root.ToJsonString(JsonOptions), cancellationToken).ConfigureAwait(false);
                if (File.Exists(_paths.ConfigPath)) File.Replace(temporaryPath, _paths.ConfigPath, BackupPath);
                else File.Move(temporaryPath, _paths.ConfigPath);
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
            OperationResult startupResult = OperationResult.Success();
            if (values.ContainsKey("startup.runAtLogon"))
            {
                try { _startup.SetEnabled(_paths, _runtimeOverrides.TryGetValue("startup.runAtLogon", out var run) && run.ValueKind == JsonValueKind.True); }
                catch (Exception exception)
                {
                    try { File.Replace(BackupPath, _paths.ConfigPath, null); } catch { }
                    _runtimeOverrides.Clear();
                    foreach (var (id, value) in previousOverrides) _runtimeOverrides[id] = value;
                    _rollbackOverrides = null;
                    return OperationResult.Failure(new(ErrorCodes.StorageUnavailable, "启动项注册失败：" + exception.Message, "startup.runAtLogon"));
                }
            }
            return OperationResult.Success();
        }
        finally
        {
            _lock.Release();
        }
    }

    private OperationResult ValidateStartup(IReadOnlyDictionary<string, object?> effectiveValues) =>
        effectiveValues.TryGetValue("startup.runAtLogon", out var value) && value is bool
            ? OperationResult.Success()
            : effectiveValues.ContainsKey("startup.runAtLogon")
                ? OperationResult.Failure(new(ErrorCodes.ValidationFailed, "开机自启必须是开关值。", "startup.runAtLogon"))
                : OperationResult.Success();

    public async Task<OperationResult> RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(BackupPath)) return OperationResult.Success();
            File.Replace(BackupPath, _paths.ConfigPath, null);
            if (_rollbackOverrides is { } rollback)
            {
                _runtimeOverrides.Clear();
                foreach (var (id, value) in rollback) _runtimeOverrides[id] = value;
                _rollbackOverrides = null;
                if (rollback.ContainsKey("startup.runAtLogon"))
                {
                    try { _startup.SetEnabled(_paths, _runtimeOverrides.TryGetValue("startup.runAtLogon", out var run) && run.ValueKind == JsonValueKind.True); }
                    catch (Exception exception)
                    {
                        return OperationResult.Failure(new(ErrorCodes.StorageUnavailable, "启动项回滚失败：" + exception.Message));
                    }
                }
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

    public async Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        if (values.TryGetValue("startup.runAtLogon", out var value) && value is bool enabled)
        {
            try { _startup.SetEnabled(_paths, enabled); }
            catch (Exception exception)
            {
                throw new InvalidOperationException("启动项注册失败：" + exception.Message, exception);
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private JsonObject ReadJson()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_paths.ConfigPath));
            return JsonSerializer.Deserialize<JsonObject>(document.RootElement.GetRawText(), JsonOptions)
                ?? throw new ConfigurationException("配置根必须是 JSON 对象。");
        }
        catch (JsonException)
        {
            throw new ConfigurationException("配置损坏，请修正 Config/appsettings.json。");
        }
    }

    private static Dictionary<string, JsonElement> BuildEffectiveValues(AppConfiguration configuration)
    {
        var overrides = configuration.Settings ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        values["host.modules"] = overrides.TryGetValue("host.modules", out var modules)
            ? modules.Clone()
            : JsonSerializer.SerializeToElement(configuration.Modules);
        values["host.providers"] = overrides.TryGetValue("host.providers", out var providers)
            ? providers.Clone()
            : JsonSerializer.SerializeToElement(configuration.Providers);
        values["storage.filename"] = overrides.TryGetValue("storage.filename", out var filename)
            ? filename.Clone()
            : JsonSerializer.SerializeToElement(configuration.Storage.Filename);
        values["storage.location"] = overrides.TryGetValue("storage.location", out var location)
            ? location.Clone()
            : JsonSerializer.SerializeToElement(configuration.Storage.Location);
        foreach (var definition in HostDefinitions)
            if (overrides.TryGetValue(definition.Id, out var value))
                values[definition.Id] = value.Clone();
        return values;
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
