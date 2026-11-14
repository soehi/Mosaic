using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Core;

namespace Mosaic.Desktop;

internal sealed class HostSettingSource(AppPaths paths) : IHostSettingSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string ModuleId => "host";
    public IReadOnlyList<SettingDefinition> Definitions => SettingCatalog.ForModule(ModuleId);

    public object? GetValue(string id)
    {
        var node = ReadJson()["settings"]?[id];
        return node is null ? null : JsonNodeToObject(node);
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
            await File.WriteAllTextAsync(temporaryPath, root.ToJsonString(JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Replace(temporaryPath, paths.ConfigPath, null);
            return OperationResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return OperationResult.Failure(new(ErrorCodes.StorageUnavailable, "宿主配置保存失败：" + exception.Message));
        }
        finally
        {
            _lock.Release();
        }
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
