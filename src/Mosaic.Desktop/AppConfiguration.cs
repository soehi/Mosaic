using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mosaic.Desktop;

internal sealed record StorageConfiguration(string Engine, string Location, string Filename);

internal sealed record AppConfiguration(
    int ConfigVersion,
    string[] Modules,
    Dictionary<string, string> Providers,
    StorageConfiguration Storage,
    IReadOnlyDictionary<string, JsonElement>? Settings = null,
    string[]? AppliedMigrations = null)
{
    private const string PresenceMigration = "desktop-presence-v1";
    private const string ReleaseSummaryMigration = "release-summary-v1";
    private const string FloatingMigration="floating-windows-v1";
    private static readonly string[] KnownMigrations = [PresenceMigration, ReleaseSummaryMigration,FloatingMigration];

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
        if (configuration is null) throw new ConfigurationException("配置根必须是 JSON 对象。");
        var original = configuration;
        var appliedMigrations = configuration.AppliedMigrations ?? [];
        ValidateCoreConfiguration(configuration,
            validateDependencies: KnownMigrations.All(id => appliedMigrations.Contains(id, StringComparer.Ordinal)));

        if (!appliedMigrations.Contains(PresenceMigration, StringComparer.Ordinal))
            configuration = ApplyPresenceMigration(configuration);
        if (!appliedMigrations.Contains(ReleaseSummaryMigration, StringComparer.Ordinal))
            configuration = ApplyReleaseSummaryMigration(configuration);

        if (!appliedMigrations.Contains(FloatingMigration,StringComparer.Ordinal))
            configuration=ApplyFloatingMigration(configuration);

        if (configuration != original)
        {
            ValidateCoreConfiguration(configuration, validateDependencies: true);
            WriteConfiguration(paths, configuration);
        }

        return configuration with
        {
            Settings = configuration.Settings ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            AppliedMigrations = configuration.AppliedMigrations ?? []
        };
    }

    private static void ValidateCoreConfiguration(AppConfiguration configuration, bool validateDependencies)
    {
        if (configuration is not { ConfigVersion: 1, Modules: not null, Providers: not null, Storage: not null })
            throw new ConfigurationException("配置版本或必填内容无效。");
        if (configuration.AppliedMigrations is { } migrations &&
            migrations.Distinct(StringComparer.Ordinal).Count() != migrations.Length)
            throw new ConfigurationException("配置迁移标记包含重复项。");
        var validation = HostConfigurationValidator.ValidateDeclarations(configuration.Modules, configuration.Providers);
        if (!validation.IsSuccess)
            throw new ConfigurationException(validation.Error!.Message);
        ValidateEffectiveHostConfiguration(configuration, validateDependencies);

        var storage = configuration.Storage;
        if (storage.Engine != "sqlite" ||
            storage.Location != "local-app-data" ||
            !IsValidDatabaseFilename(storage.Filename))
            throw new ConfigurationException("存储配置无效，数据库名称必须是合法的单个文件名。");
    }

    private static void ValidateEffectiveHostConfiguration(AppConfiguration configuration, bool validateDependencies)
    {
        var modules = ReadModuleOverride(configuration) ?? configuration.Modules;
        var providers = ReadProviderOverride(configuration) ?? configuration.Providers;
        var validation = validateDependencies
            ? HostConfigurationValidator.Validate(modules, providers)
            : HostConfigurationValidator.ValidateDeclarations(modules, providers);
        if (!validation.IsSuccess)
            throw new ConfigurationException(validation.Error!.Message);
    }

    private static AppConfiguration ApplyPresenceMigration(AppConfiguration configuration)
    {
        var settings = configuration.Settings is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(configuration.Settings, StringComparer.Ordinal);
        var hasModuleOverride = settings.ContainsKey("host.modules");
        var hasProviderOverride = settings.ContainsKey("host.providers");
        var modules = ReadModuleOverride(configuration)?.ToList() ?? configuration.Modules.ToList();
        var providers = ReadProviderOverride(configuration) is { } providerOverride
            ? new Dictionary<string, string>(providerOverride, StringComparer.Ordinal)
            : new Dictionary<string, string>(configuration.Providers, StringComparer.Ordinal);
        var moduleChanged = false;
        var providerChanged = false;

        if (modules.Contains("tasks-ui", StringComparer.Ordinal))
        {
            if (!modules.Contains("desktop-presence", StringComparer.Ordinal))
            {
                modules.Add("desktop-presence");
                moduleChanged = true;
            }
            providerChanged |= providers.TryAdd("presence.tasks-view@1", "tasks-ui");
            providerChanged |= providers.TryAdd("desktop.presence@1", "desktop-presence");
        }

        if (moduleChanged && hasModuleOverride)
            settings["host.modules"] = JsonSerializer.SerializeToElement(modules.ToArray());
        if (providerChanged)
        {
            // A modules override can enable a module that is absent from the top-level baseline.
            // Keep the effective provider map in the same layer so both lists remain self-consistent.
            if (hasModuleOverride || hasProviderOverride)
                settings["host.providers"] = JsonSerializer.SerializeToElement(providers);
        }

        return configuration with
        {
            Modules = hasModuleOverride ? configuration.Modules : modules.ToArray(),
            Providers = hasModuleOverride ? configuration.Providers : providers,
            Settings = settings,
            AppliedMigrations = MarkMigration(configuration, PresenceMigration)
        };
    }

    private static AppConfiguration ApplyReleaseSummaryMigration(AppConfiguration configuration)
    {
        var settings = configuration.Settings is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(configuration.Settings, StringComparer.Ordinal);
        var hasModuleOverride = settings.ContainsKey("host.modules");
        var hasProviderOverride = settings.ContainsKey("host.providers");
        var modules = ReadModuleOverride(configuration)?.ToList() ?? configuration.Modules.ToList();
        var providers = ReadProviderOverride(configuration) is { } providerOverride
            ? new Dictionary<string, string>(providerOverride, StringComparer.Ordinal)
            : new Dictionary<string, string>(configuration.Providers, StringComparer.Ordinal);
        var tasksEnabled = modules.Contains("tasks-core", StringComparer.Ordinal);
        var settingsEnabled = modules.Contains("settings-core", StringComparer.Ordinal);
        var moduleChanged = false;
        var providerChanged = false;

        if (tasksEnabled && settingsEnabled)
        {
            if (!modules.Contains("release-summary", StringComparer.Ordinal))
            {
                modules.Add("release-summary");
                moduleChanged = true;
            }
            providerChanged |= providers.TryAdd("tasks.create-once@1", "tasks-core");
            providerChanged |= providers.TryAdd("release.summary@1", "release-summary");
        }

        if (moduleChanged && hasModuleOverride)
            settings["host.modules"] = JsonSerializer.SerializeToElement(modules.ToArray());
        if (providerChanged && (hasModuleOverride || hasProviderOverride))
            settings["host.providers"] = JsonSerializer.SerializeToElement(providers);

        return configuration with
        {
            Modules = hasModuleOverride ? configuration.Modules : modules.ToArray(),
            Providers = hasModuleOverride || hasProviderOverride ? configuration.Providers : providers,
            Settings = settings,
            AppliedMigrations = MarkMigration(configuration, ReleaseSummaryMigration)
        };
    }

    private static AppConfiguration ApplyFloatingMigration(AppConfiguration configuration)
    {
        var settings=configuration.Settings is null?new Dictionary<string,JsonElement>(StringComparer.Ordinal):new Dictionary<string,JsonElement>(configuration.Settings,StringComparer.Ordinal);
        var overrideModules=settings.ContainsKey("host.modules");var overrideProviders=settings.ContainsKey("host.providers");
        var modules=(ReadModuleOverride(configuration)??configuration.Modules).ToList();
        var providers=new Dictionary<string,string>(ReadProviderOverride(configuration)??configuration.Providers,StringComparer.Ordinal);
        if(modules.Contains("tasks-core",StringComparer.Ordinal))providers.TryAdd("tasks.changes@1","tasks-core");
        if(modules.Contains("tasks-ui",StringComparer.Ordinal))
        {
            providers.TryAdd("floating.task-content@1","tasks-ui");providers.TryAdd("floating.detach-source@1","tasks-ui");
        }
        if(new[]{"tasks-ui","tasks-core","tasks-sqlite","settings-core","settings-sqlite","storage-sqlite"}.All(modules.Contains))
        {
            foreach(var id in new[]{"floating-windows-sqlite","floating-windows"})if(!modules.Contains(id,StringComparer.Ordinal))modules.Add(id);
            providers.TryAdd("floating.repository@1","floating-windows-sqlite");providers.TryAdd("floating.windows@1","floating-windows");
        }
        if(overrideModules)settings["host.modules"]=JsonSerializer.SerializeToElement(modules.ToArray());
        if(overrideModules||overrideProviders)settings["host.providers"]=JsonSerializer.SerializeToElement(providers);
        return configuration with {Modules=overrideModules?configuration.Modules:modules.ToArray(),Providers=overrideModules||overrideProviders?configuration.Providers:providers,Settings=settings,AppliedMigrations=MarkMigration(configuration,FloatingMigration)};
    }

    private static string[] MarkMigration(AppConfiguration configuration, string migration) =>
        (configuration.AppliedMigrations ?? [])
            .Append(migration)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string[]? ReadModuleOverride(AppConfiguration configuration)
    {
        if (configuration.Settings is not { } settings || !settings.TryGetValue("host.modules", out var value))
            return null;
        try
        {
            return value.Deserialize<string[]>()
                ?? throw new ConfigurationException("宿主模块覆盖必须是字符串数组。");
        }
        catch (JsonException) { throw new ConfigurationException("宿主模块覆盖必须是字符串数组。"); }
    }

    private static Dictionary<string, string>? ReadProviderOverride(AppConfiguration configuration)
    {
        if (configuration.Settings is not { } settings || !settings.TryGetValue("host.providers", out var value))
            return null;
        try
        {
            return value.Deserialize<Dictionary<string, string>>()
                ?? throw new ConfigurationException("宿主能力绑定覆盖必须是字符串映射。");
        }
        catch (JsonException) { throw new ConfigurationException("宿主能力绑定覆盖必须是字符串映射。"); }
    }
    internal static bool IsValidDatabaseFilename(string filename) =>
        !string.IsNullOrWhiteSpace(filename) &&
        filename is not "." and not ".." &&
        filename == Path.GetFileName(filename) &&
        filename.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !filename.EndsWith('.') &&
        !filename.EndsWith(' ') &&
        !IsReserved(filename);
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

    private static void WriteConfiguration(AppPaths paths, AppConfiguration configuration)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var root = new Dictionary<string, object?>
        {
            ["configVersion"] = configuration.ConfigVersion,
            ["appliedMigrations"] = configuration.AppliedMigrations ?? [],
            ["modules"] = configuration.Modules,
            ["providers"] = configuration.Providers,
            ["storage"] = configuration.Storage,
            ["settings"] = configuration.Settings
        };
        var temporaryPath = paths.ConfigPath + ".tmp";
        var backupPath = paths.ConfigPath + ".pre-migration";
        try
        {
            if (File.Exists(paths.ConfigPath)) File.Copy(paths.ConfigPath, backupPath, overwrite: true);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(root, options));
            File.Replace(temporaryPath, paths.ConfigPath, backupPath);
        }
        catch (Exception exception)
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { /* A leftover temporary file must not mask the original failure. */ }
            throw new ConfigurationException("升级模块配置失败：" + exception.Message);
        }
    }
}

internal sealed class ConfigurationException(string message) : Exception(message);

internal sealed record AppPaths(string Root)
{
    internal string ConfigDirectory => Path.Combine(Root, "Config");
    internal string ConfigPath => Path.Combine(ConfigDirectory, "appsettings.json");
    internal string Logs => Path.Combine(Root, "Logs");
    internal string Database(StorageConfiguration storage) =>
        Path.Combine(
            storage.Location == "local-app-data" ? Path.Combine(Root, "Data") : Path.TrimEndingDirectorySeparator(storage.Location),
            storage.Filename);
    internal static AppPaths FromArguments(string[] args)
    {
        if (args.Length == 0) return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mosaic"))));
        if (args.Length == 2 && args[0] == "--data-root" && Path.IsPathFullyQualified(args[1])) return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1])));
        throw new ConfigurationException("支持的参数：--data-root <绝对路径>。");
    }
}
