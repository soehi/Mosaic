using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mosaic.Desktop;

internal sealed record StorageConfiguration(string Engine, string Location, string Filename);
internal sealed record AppConfiguration(int ConfigVersion, string[] Modules, Dictionary<string, string> Providers, StorageConfiguration Storage)
{
    internal static AppConfiguration Load(AppPaths paths)
    {
        Directory.CreateDirectory(paths.ConfigDirectory);
        if (!File.Exists(paths.ConfigPath)) File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), paths.ConfigPath);
        AppConfiguration? configuration;
        try
        {
            var json = File.ReadAllText(paths.ConfigPath);
            using var document = JsonDocument.Parse(json);
            RejectDuplicateProperties(document.RootElement);
            configuration = JsonSerializer.Deserialize<AppConfiguration>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            });
        }
        catch (JsonException) { throw new ConfigurationException("配置 JSON 格式无效，请修正 Config/appsettings.json。"); }
        if (configuration is not { ConfigVersion: 1, Modules: not null, Providers: not null, Storage: not null })
            throw new ConfigurationException("配置版本或必填内容无效。");
        if (configuration.Modules.Distinct(StringComparer.Ordinal).Count() != configuration.Modules.Length || configuration.Modules.Any(m => m is null || !BuiltInModuleCatalog.Definitions.ContainsKey(m)))
            throw new ConfigurationException("模块列表包含未知或重复的模块。");
        if (configuration.Providers.Any(p =>
                p.Value is null ||
                !configuration.Modules.Contains(p.Value, StringComparer.Ordinal) ||
                !BuiltInModuleCatalog.Definitions.TryGetValue(p.Value, out var definition) ||
                !definition.Provides.Any(c => c.ToString() == p.Key)))
            throw new ConfigurationException("能力提供者配置无效。");
        var storage = configuration.Storage;
        if (storage.Engine != "sqlite" || storage.Location != "local-app-data" || string.IsNullOrWhiteSpace(storage.Filename) ||
            storage.Filename is "." or ".." || storage.Filename != Path.GetFileName(storage.Filename) || storage.Filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            storage.Filename.EndsWith('.') || storage.Filename.EndsWith(' ') || IsReserved(storage.Filename))
            throw new ConfigurationException("存储配置无效，数据库名称必须是合法的单个文件名。");
        return configuration;
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ConfigurationException("配置包含重复字段或能力提供者。");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
    private static bool IsReserved(string filename)
    {
        var name = filename.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" || (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9');
    }
}

internal sealed class ConfigurationException(string message) : Exception(message);

internal sealed record AppPaths(string Root)
{
    internal string ConfigDirectory => Path.Combine(Root, "Config");
    internal string ConfigPath => Path.Combine(ConfigDirectory, "appsettings.json");
    internal string Logs => Path.Combine(Root, "Logs");
    internal string Database(string filename) => Path.Combine(Root, "Data", filename);
    internal static AppPaths FromArguments(string[] args)
    {
        if (args.Length == 0) return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mosaic"))));
        if (args.Length == 2 && args[0] == "--data-root" && Path.IsPathFullyQualified(args[1])) return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1])));
        throw new ConfigurationException("支持的参数：--data-root <绝对路径>。");
    }
}
