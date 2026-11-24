using System.Text.Json;
using System.Text.Json.Serialization;
using Mosaic.Maintenance.Contracts;

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
    private const string AuthSyncMigration = "auth-sync-v1";
    private const string MaintenanceMigration = "maintenance-v1";
    private static readonly string[] KnownMigrations = [PresenceMigration, ReleaseSummaryMigration,FloatingMigration,AuthSyncMigration,MaintenanceMigration];

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
        configuration = RepairProviderBindings(configuration);
        ValidateCoreConfiguration(configuration,
            validateDependencies: KnownMigrations.All(id => appliedMigrations.Contains(id, StringComparer.Ordinal)));

        if (!appliedMigrations.Contains(PresenceMigration, StringComparer.Ordinal))
            configuration = ApplyPresenceMigration(configuration);
        if (!appliedMigrations.Contains(ReleaseSummaryMigration, StringComparer.Ordinal))
            configuration = ApplyReleaseSummaryMigration(configuration);

        if (!appliedMigrations.Contains(FloatingMigration,StringComparer.Ordinal))
            configuration=ApplyFloatingMigration(configuration);

        if (!appliedMigrations.Contains(AuthSyncMigration, StringComparer.Ordinal))
            configuration = ApplyAuthSyncMigration(configuration);

        if (!appliedMigrations.Contains(MaintenanceMigration, StringComparer.Ordinal))
            configuration = ApplyMaintenanceMigration(configuration);

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

    // Read-only counterpart of Load, used by the maintenance page before a repair. Load repairs and
    // rewrites the file, so it can never report a problem; Inspect only reports what Load would still
    // have to rewrite (pending migrations, bindings missing from the file) or reject, and never writes.
    internal static ConfigurationHealthReport Inspect(AppPaths paths)
    {
        if (!File.Exists(paths.ConfigPath))
            return new(false, ["配置文件 Config/appsettings.json 不存在。"]);
        AppConfiguration? configuration;
        try
        {
            var json = File.ReadAllText(paths.ConfigPath);
            using var document = JsonDocument.Parse(json);
            RejectDuplicateProperties(document.RootElement);
            configuration = JsonSerializer.Deserialize<AppConfiguration>(json, InspectionOptions);
        }
        catch (ConfigurationException exception) { return new(false, [exception.Message]); }
        catch (JsonException) { return new(false, ["配置 JSON 格式无效，请修正 Config/appsettings.json。"]); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(false, ["无法读取配置文件：" + exception.Message]);
        }
        if (configuration is null) return new(false, ["配置根必须是 JSON 对象。"]);

        var issues = new List<string>();
        var applied = configuration.AppliedMigrations ?? [];
        var repaired = RepairProviderBindings(configuration);
        var bindingsComplete = true;
        try { ValidateCoreConfiguration(repaired, validateDependencies: true); }
        catch (ConfigurationException exception)
        {
            bindingsComplete = false;
            issues.Add(exception.Message);
        }
        var pending = KnownMigrations.Where(id => !applied.Contains(id, StringComparer.Ordinal)).ToArray();
        if (pending.Length > 0) issues.Add("有待应用的配置迁移：" + string.Join("、", pending) + "。");
        if (bindingsComplete)
        {
            var storage = EffectiveStorage(repaired);
            if (storage.Location != "local-app-data" && !Path.IsPathFullyQualified(storage.Location))
                issues.Add("存储位置必须是 local-app-data 或绝对路径。");
            else if (!IsValidDatabaseFilename(storage.Filename))
                issues.Add("存储配置无效，数据库名称必须是合法的单个文件名。");
        }
        return new(issues.Count == 0, issues);
    }

    private static readonly JsonSerializerOptions InspectionOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    // Mirrors the runtime overrides Bootstrapper applies through HostSettingSource.
    private static StorageConfiguration EffectiveStorage(AppConfiguration configuration)
    {
        static string? Text(IReadOnlyDictionary<string, JsonElement>? settings, string key) =>
            settings is not null && settings.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        return new StorageConfiguration(
            "sqlite",
            Text(configuration.Settings, "storage.location") ?? configuration.Storage.Location,
            Text(configuration.Settings, "storage.filename") ?? configuration.Storage.Filename);
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

    // Runs on every launch, independent of migration markers: a module upgrade that adds a Provided
    // capability would otherwise break every existing installation until someone shipped a matching
    // migration (0.16.0 shipped tasks.transfer-repository@1 / tasks.transfer@1 with no binding, and
    // installs upgraded from ≤0.15.3 failed to start with "Provider binding is missing or
    // incompatible"). Bindings are derivable from the built-in manifests, so fill in any capability
    // of an enabled module that has no binding. Existing entries are never overwritten: a binding
    // the user set explicitly wins even when it deliberately differs from the built-in default.
    private static AppConfiguration RepairProviderBindings(AppConfiguration configuration)
    {
        // Shape errors (null lists) surface through ValidateCoreConfiguration with the usual message.
        if (configuration is not { Modules: not null, Providers: not null }) return configuration;
        var settings = configuration.Settings is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(configuration.Settings, StringComparer.Ordinal);
        var overrideModules = settings.ContainsKey("host.modules");
        var overrideProviders = settings.ContainsKey("host.providers");
        var modules = ReadModuleOverride(configuration) ?? configuration.Modules;
        var providers = new Dictionary<string, string>(ReadProviderOverride(configuration) ?? configuration.Providers, StringComparer.Ordinal);
        var changed = false;
        foreach (var moduleId in modules)
        {
            // Unknown module ids are reported by declaration validation; nothing to derive here.
            if (!BuiltInModuleCatalog.Definitions.TryGetValue(moduleId, out var definition)) continue;
            foreach (var capability in definition.Provides)
                changed |= providers.TryAdd(capability.ToString(), definition.Id);
        }
        if (!changed) return configuration;
        if (overrideModules || overrideProviders)
            settings["host.providers"] = JsonSerializer.SerializeToElement(providers);
        return configuration with
        {
            Modules = overrideModules ? configuration.Modules : modules,
            Providers = overrideModules || overrideProviders ? configuration.Providers : providers,
            Settings = settings
        };
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

    // Adds the optional account/backup modules to existing installations. Nothing activates the
    // network by itself: without a configured server the account page stays signed out and offline.
    private static AppConfiguration ApplyAuthSyncMigration(AppConfiguration configuration)
    {
        var settings = configuration.Settings is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(configuration.Settings, StringComparer.Ordinal);
        var overrideModules = settings.ContainsKey("host.modules");
        var overrideProviders = settings.ContainsKey("host.providers");
        var modules = (ReadModuleOverride(configuration) ?? configuration.Modules).ToList();
        var providers = new Dictionary<string, string>(ReadProviderOverride(configuration) ?? configuration.Providers, StringComparer.Ordinal);
        if (modules.Contains("settings-core", StringComparer.Ordinal) && modules.Contains("tasks-core", StringComparer.Ordinal))
        {
            foreach (var id in new[] { "auth-http", "auth-windows", "auth-core", "sync-http", "sync-core" })
                if (!modules.Contains(id, StringComparer.Ordinal)) modules.Add(id);
            if (modules.Contains("settings-ui", StringComparer.Ordinal) && !modules.Contains("auth-ui", StringComparer.Ordinal))
                modules.Add("auth-ui");
            providers.TryAdd("auth.transport@1", "auth-http");
            providers.TryAdd("auth.secrets@1", "auth-windows");
            providers.TryAdd("auth.api@1", "auth-core");
            providers.TryAdd("sync.transport@1", "sync-http");
            providers.TryAdd("sync.api@1", "sync-core");
            // 0.16.0 introduced the task transfer pipeline together with the auth/sync modules but
            // never bound its two new capabilities; ModuleGraph.Resolve demands a binding for every
            // Provided capability, so existing installs failed to start with
            // "Provider binding is missing or incompatible: tasks.transfer-repository@1".
            providers.TryAdd("tasks.transfer-repository@1", "tasks-sqlite");
            providers.TryAdd("tasks.transfer@1", "tasks-core");
        }
        if (overrideModules) settings["host.modules"] = JsonSerializer.SerializeToElement(modules.ToArray());
        if (overrideModules || overrideProviders) settings["host.providers"] = JsonSerializer.SerializeToElement(providers);
        return configuration with
        {
            Modules = overrideModules ? configuration.Modules : modules.ToArray(),
            Providers = overrideModules || overrideProviders ? configuration.Providers : providers,
            Settings = settings,
            AppliedMigrations = MarkMigration(configuration, AuthSyncMigration)
        };
    }

    private static AppConfiguration ApplyMaintenanceMigration(AppConfiguration configuration)
    {
        var settings = configuration.Settings is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(configuration.Settings, StringComparer.Ordinal);
        var overrideModules = settings.ContainsKey("host.modules");
        var overrideProviders = settings.ContainsKey("host.providers");
        var modules = (ReadModuleOverride(configuration) ?? configuration.Modules).ToList();
        var providers = new Dictionary<string, string>(ReadProviderOverride(configuration) ?? configuration.Providers, StringComparer.Ordinal);
        if (modules.Contains("settings-ui", StringComparer.Ordinal))
        {
            // Existing installs without the maintenance modules would fail ModuleGraph.Resolve because
            // storage-sqlite now also provides storage.relocation@1 and maintenance-ui needs a binding.
            if (!modules.Contains("storage-sqlite", StringComparer.Ordinal)) modules.Add("storage-sqlite");
            if (!modules.Contains("maintenance-ui", StringComparer.Ordinal)) modules.Add("maintenance-ui");
            providers.TryAdd("storage.relocation@1", "storage-sqlite");
            providers.TryAdd("app.maintenance@1", "maintenance-ui");
        }
        if (overrideModules) settings["host.modules"] = JsonSerializer.SerializeToElement(modules.ToArray());
        if (overrideModules || overrideProviders) settings["host.providers"] = JsonSerializer.SerializeToElement(providers);
        return configuration with
        {
            Modules = overrideModules ? configuration.Modules : modules.ToArray(),
            Providers = overrideModules || overrideProviders ? configuration.Providers : providers,
            Settings = settings,
            AppliedMigrations = MarkMigration(configuration, MaintenanceMigration)
        };
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
            // 显式写成小写键：StorageConfiguration 的属性是 PascalCase，直接序列化会产出
            // "Engine"/"Location"/"Filename"，与输入格式不一致，外部工具和按小写键读取的代码会拿到 null。
            ["storage"] = new
            {
                engine = configuration.Storage.Engine,
                location = configuration.Storage.Location,
                filename = configuration.Storage.Filename
            },
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
    internal string PeekFilterPreference => Path.Combine(ConfigDirectory, "task-peek-filter.txt");
    internal string Logs => Path.Combine(Root, "Logs");
    // DPAPI-protected credentials for optional sign-in. Never read by settings or backups.
    internal string AuthSecrets => Path.Combine(Root, "Auth", "credentials.dat");
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
