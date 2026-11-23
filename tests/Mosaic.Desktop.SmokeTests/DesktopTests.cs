using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;

namespace Mosaic.Desktop.SmokeTests;

public sealed partial class DesktopTests
{
    private static string Root
    {
        get { var p = new DirectoryInfo(AppContext.BaseDirectory); while (p is not null && !File.Exists(Path.Combine(p.FullName, "Mosaic.sln"))) p = p.Parent; return p!.FullName; }
    }
    private static string Executable => Environment.GetEnvironmentVariable("MOSAIC_SMOKE_EXE") ?? Path.Combine(Root, "artifacts/local-run/Mosaic.Desktop.exe");
    private static string NewRoot() { var path = Path.Combine(Root, "artifacts/smoke-data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static Process StartExisting(string dataRoot, bool hidden = false)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false };
        if (hidden)
        {
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.CreateNoWindow = true;
        }
        info.ArgumentList.Add("--data-root");
        info.ArgumentList.Add(dataRoot);
        return Process.Start(info)!;
    }
    private static Process Start(string dataRoot, bool presence = false)
    {
        var configPath = Path.Combine(dataRoot, "Config/appsettings.json");
        if (!File.Exists(configPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(Root, "src/Mosaic.Desktop/appsettings.json")))!;
            if (!presence)
            {
                config["modules"] = JsonSerializer.SerializeToElement(config["modules"].Deserialize<string[]>()!.Where(id => id != "desktop-presence"));
                config["providers"] = JsonSerializer.SerializeToElement(config["providers"].Deserialize<Dictionary<string, string>>()!.Where(item => item.Value != "desktop-presence").ToDictionary());
            }
            File.WriteAllText(configPath, JsonSerializer.Serialize(config));
        }
        return StartExisting(dataRoot);
    }
    private static async Task UntilCondition(Func<bool> condition, string description)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            try { if (condition()) return; } catch (IOException) { } catch (JsonException) { }
            await Task.Delay(75);
        }
        throw new TimeoutException(description);
    }
    private static bool HasAppliedPresenceMigration(string configPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        return document.RootElement.TryGetProperty("appliedMigrations", out var migrations) &&
            migrations.EnumerateArray().Any(item => item.GetString() == "desktop-presence-v1");
    }
    private static async Task<T> Until<T>(Func<T?> get, string description) where T : class
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            try { if (get() is { } result) return result; } catch (ElementNotAvailableException) { }
            await Task.Delay(75);
        }
        throw new TimeoutException(description);
    }
    private static AutomationElement? Window(Process process) => AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
    private static AutomationElement? Id(AutomationElement parent, string id) => parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
    private static AutomationElement? Name(AutomationElement parent, string name) => parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
    // 任务行的自动化名称是整行无障碍标签（标题 + 完成状态 + 附件/时间），不再等于标题；
    // 按名称找元素会命中行内 TextBlock（没有 SelectionItemPattern）。这里限定列表项控件类型。
    private static AutomationElement? TaskRow(AutomationElement window, string title) =>
        Id(window, "TaskList") is { } list
            ? list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>()
                .FirstOrDefault(element => element.Current.Name.Contains(title, StringComparison.Ordinal))
            : null;
    private static void Click(AutomationElement element) => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    private static void Select(AutomationElement element) => ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
    private static void Text(AutomationElement element, string value) => ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    private static Task<AutomationElement> Control(Process process, string id) => Until(() => Window(process) is { } w ? Id(w, id) : null, "Missing control: " + id);
    private static Task<AutomationElement> Message(Process process, string text) => Until(() => Window(process) is { } w && Id(w, "TaskMessage") is { } m && m.Current.Name.Contains(text, StringComparison.Ordinal) ? m : null, "Missing message: " + text);
    private static async Task Close(Process process)
    {
        process.Refresh(); Assert.True(process.CloseMainWindow());
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); Assert.Equal(0, process.ExitCode);
    }
    private static void Cleanup(Process? process) { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); process?.Dispose(); }

    [Fact]
    public async Task Published_application_crud_reopen_and_single_instance()
    {
        var root = NewRoot(); Process? process = null;
        try
        {
            process = Start(root);
            var title = await Control(process, "TaskTitle"); Text(title, "中文烟雾任务 😀");
            Text(await Control(process, "TaskNotes"), "重启后应保留的备注"); Click(await Control(process, "SaveTask")); await Message(process, "已保存");
            Text(title, "编辑后的任务"); Click(await Control(process, "SaveTask")); await Message(process, "已保存");
            Click(await Control(process, "ToggleTask")); await Message(process, "已标记完成");
            Click(await Control(process, "ToggleTask")); await Message(process, "已恢复");
            using (var secondary = Start(root + Path.DirectorySeparatorChar)) { await secondary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, secondary.ExitCode); }
            Assert.False(process.HasExited);
            await Close(process); process.Dispose(); process = Start(root);
            await Control(process, "TaskTitle");
            var item = await Until(() => Window(process) is { } w ? TaskRow(w, "编辑后的任务") : null, "Persisted task not found");
            Select(item);
            await Until(() => ((ValuePattern)Id(Window(process)!, "TaskTitle")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == "编辑后的任务" ? new object() : null, "Editor did not load");
            Assert.Equal("重启后应保留的备注", ((ValuePattern)(await Control(process, "TaskNotes")).GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            Click(await Control(process, "DeleteTask"));
            var confirm = await Until(() => Window(process)?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "6")), "Delete confirmation not found");
            Click(confirm); await Message(process, "已删除");
            await Close(process); process.Dispose(); process = null;
            Assert.True(File.Exists(Path.Combine(root, "Data/mosaic.sqlite")));
        }
        finally { Cleanup(process); }
    }

    [Fact]
    public async Task Presence_parks_keeps_draft_restores_on_second_launch_and_exits_explicitly()
    {
        var root = NewRoot(); using var process = Start(root, presence: true);
        try
        {
            Text(await Control(process, "TaskTitle"), "未保存的驻留草稿");
            process.Refresh(); Assert.True(process.CloseMainWindow());
            var edge = await Until(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "PresenceEdge"))), "Missing edge entry");
            Click(await Until(() => Id(edge, "PresenceTrigger"), "Missing overview trigger"));
            var popup = await Until(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "PresencePopup"))), "Missing task overview");
            Assert.Null(Id(popup, "TaskTitle"));
            Click(await Until(() => Id(popup, "PresenceRestore"), "Missing restore control"));
            Assert.Equal("未保存的驻留草稿", ((ValuePattern)(await Control(process, "TaskTitle")).GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            using (var secondary = Start(root, presence: true)) await secondary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            // Clear only the unsaved test input so explicit exit can proceed without a prompt.
            Text(await Control(process, "TaskTitle"), "");
            process.Refresh(); Assert.True(process.CloseMainWindow());
            edge = await Until(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "PresenceEdge"))), "Missing second edge entry");
            Click(await Until(() => Id(edge, "PresenceTrigger"), "Missing second overview trigger"));
            popup = await Until(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "PresencePopup"))), "Missing second overview");
            Click(await Until(() => Id(popup, "PresenceMore"), "Missing overview more control"));
            var exit = await Until(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "PresenceExit")), "Missing exit menu");
            Click(exit);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
        }
        finally { Cleanup(process); }
    }

    [Theory]
    [InlineData("top-level")]
    [InlineData("host-overrides")]
    [InlineData("host-module-only")]
    [InlineData("host-provider-only")]
    [InlineData("already-migrated-disabled")]
    [InlineData("invalid-not-rewritten")]
    public async Task Configuration_presence_migration_is_validated_persisted_and_one_time(string scenario)
    {
        var root = NewRoot();
        var configDirectory = Path.Combine(root, "Config");
        Directory.CreateDirectory(configDirectory);
        var configPath = Path.Combine(configDirectory, "appsettings.json");
        var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(Path.Combine(Root, "src/Mosaic.Desktop/appsettings.json")))!;
        var modules = config["modules"].Deserialize<string[]>()!
            .Where(id => scenario is "already-migrated-disabled" or "invalid-not-rewritten" || id != "desktop-presence")
            .ToArray();
        var providers = config["providers"].Deserialize<Dictionary<string, string>>()!
            .Where(item => scenario == "already-migrated-disabled"
                ? item.Key != "desktop.presence@1"
                : item.Key is not "presence.tasks-view@1" and not "desktop.presence@1")
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        config["modules"] = JsonSerializer.SerializeToElement(modules);
        config["providers"] = JsonSerializer.SerializeToElement(providers);
        if (scenario is "top-level" or "host-overrides" or "host-module-only" or "host-provider-only" or "invalid-not-rewritten")
            config.Remove("appliedMigrations");
        if (scenario is "host-overrides" or "host-module-only" or "host-provider-only")
        {
            var settings = new Dictionary<string, object?>();
            if (scenario is "host-overrides" or "host-module-only") settings["host.modules"] = modules;
            if (scenario is "host-overrides" or "host-provider-only") settings["host.providers"] = providers;
            config["settings"] = JsonSerializer.SerializeToElement(settings);
        }
        if (scenario == "invalid-not-rewritten")
            config["storage"] = JsonSerializer.SerializeToElement(new
            {
                engine = "sqlite",
                location = "local-app-data",
                filename = "../escape.sqlite"
            });
        var seededMigrations = config.TryGetValue("appliedMigrations", out var seededMarkers)
            ? seededMarkers.EnumerateArray().Select(item => item.GetString()).ToArray()
            : [];
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        var original = File.ReadAllText(configPath);
        Process? process = null;
        try
        {
            process = StartExisting(root, hidden: true);
            if (scenario == "invalid-not-rewritten")
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                Assert.Equal(original, File.ReadAllText(configPath));
                Assert.False(File.Exists(configPath + ".pre-migration"));
                return;
            }
            if (scenario == "already-migrated-disabled")
            {
                // Every migration marker shipped with the app is already present, so no migration may
                // run again and no marker may be appended; the only permitted change is the startup
                // self-heal re-adding desktop.presence@1 from the enabled desktop-presence module.
                await UntilCondition(() => File.ReadAllText(configPath).Contains("desktop.presence@1", StringComparison.Ordinal),
                    "Startup repair did not add the desktop.presence@1 binding");
                using var healedDocument = JsonDocument.Parse(File.ReadAllText(configPath));
                Assert.Equal("desktop-presence",
                    healedDocument.RootElement.GetProperty("providers").GetProperty("desktop.presence@1").GetString());
                var applied = healedDocument.RootElement.GetProperty("appliedMigrations")
                    .EnumerateArray().Select(item => item.GetString()).ToArray();
                Assert.Equal(seededMigrations, applied);
                return;
            }

            await UntilCondition(() => HasAppliedPresenceMigration(configPath), "Presence migration was not persisted");
            using var migratedDocument = JsonDocument.Parse(File.ReadAllText(configPath));
            var migratedRoot = migratedDocument.RootElement;
            var hasModuleOverride = scenario is "host-overrides" or "host-module-only";
            var hasProviderOverride = scenario is "host-overrides" or "host-module-only" or "host-provider-only";
            var moduleScope = hasModuleOverride ? migratedRoot.GetProperty("settings") : migratedRoot;
            var providerScope = hasProviderOverride ? migratedRoot.GetProperty("settings") : migratedRoot;
            var enabledModules = moduleScope.GetProperty(hasModuleOverride ? "host.modules" : "modules")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();
            var effectiveProviders = providerScope.GetProperty(hasProviderOverride ? "host.providers" : "providers");
            Assert.Contains("desktop-presence", enabledModules);
            Assert.Equal("tasks-ui", effectiveProviders.GetProperty("presence.tasks-view@1").GetString());
            Assert.Equal("desktop-presence", effectiveProviders.GetProperty("desktop.presence@1").GetString());
            if (hasModuleOverride)
                Assert.DoesNotContain("desktop-presence", migratedRoot.GetProperty("modules").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(original, File.ReadAllText(configPath + ".pre-migration"));
        }
        finally { Cleanup(process); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Release_summary_migration_updates_the_effective_layer_once(bool overrides)
    {
        var root = NewRoot();
        var configPath = Path.Combine(root, "Config/appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(Root, "src/Mosaic.Desktop/appsettings.json")))!;
        var modules = config["modules"].Deserialize<string[]>()!.Where(id => id != "release-summary").ToArray();
        var providers = config["providers"].Deserialize<Dictionary<string,string>>()!.Where(item => item.Key is not "tasks.create-once@1" and not "release.summary@1").ToDictionary();
        config["modules"] = JsonSerializer.SerializeToElement(modules);
        config["providers"] = JsonSerializer.SerializeToElement(providers);
        config["appliedMigrations"] = JsonSerializer.SerializeToElement(new[] { "desktop-presence-v1" });
        if (overrides) config["settings"] = JsonSerializer.SerializeToElement(new Dictionary<string,object> { ["host.modules"] = modules, ["host.providers"] = providers });
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));
        using var process = StartExisting(root, hidden: true);
        try
        {
            await UntilCondition(() => File.ReadAllText(configPath).Contains("release-summary-v1", StringComparison.Ordinal), "Summary migration not persisted");
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var scope = overrides ? doc.RootElement.GetProperty("settings") : doc.RootElement;
            Assert.Contains("release-summary", scope.GetProperty(overrides ? "host.modules" : "modules").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal("tasks-core", scope.GetProperty(overrides ? "host.providers" : "providers").GetProperty("tasks.create-once@1").GetString());
            Assert.True(File.Exists(configPath + ".pre-migration"));
        }
        finally { Cleanup(process); }
    }

    [Theory]
    [InlineData("top")]
    [InlineData("both")]
    [InlineData("modules")]
    [InlineData("providers")]
    public async Task Auth_sync_migration_binds_the_task_transfer_capabilities(string scenario)
    {
        // 0.16.0/0.16.1 shipped an auth-sync-v1 migration that registered the auth/sync modules but
        // never bound tasks.transfer-repository@1 / tasks.transfer@1, so every installation upgraded
        // from ≤0.15.3 failed to start with "Provider binding is missing or incompatible:
        // tasks.transfer-repository@1". This replays that exact 0.15.3-shaped configuration.
        var root=NewRoot();var path=Path.Combine(root,"Config/appsettings.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var config=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(Root,"src/Mosaic.Desktop/appsettings.json")))!;
        var modules=config["modules"].Deserialize<string[]>()!
            .Where(id=>id is not ("auth-http" or "auth-windows" or "auth-core" or "sync-http" or "sync-core" or "auth-ui"))
            .ToArray();
        var providers=config["providers"].Deserialize<Dictionary<string,string>>()!
            .Where(item=>!item.Key.StartsWith("auth.",StringComparison.Ordinal)&&!item.Key.StartsWith("sync.",StringComparison.Ordinal))
            .ToDictionary();
        providers.Remove("tasks.transfer-repository@1");providers.Remove("tasks.transfer@1");
        config["modules"]=JsonSerializer.SerializeToElement(modules);
        config["providers"]=JsonSerializer.SerializeToElement(providers);
        config["appliedMigrations"]=JsonSerializer.SerializeToElement(new[]{"desktop-presence-v1","release-summary-v1","floating-windows-v1"});
        var settings=new Dictionary<string,object>();
        if(scenario is "both" or "modules")settings["host.modules"]=modules;
        if(scenario is "both" or "providers")settings["host.providers"]=providers;
        config["settings"]=JsonSerializer.SerializeToElement(settings);
        File.WriteAllText(path,JsonSerializer.Serialize(config));var before=File.ReadAllText(path);
        using var process=StartExisting(root,hidden:true);
        try
        {
            await UntilCondition(()=>File.ReadAllText(path).Contains("auth-sync-v1",StringComparison.Ordinal),"Auth-sync migration not persisted");
            using var doc=JsonDocument.Parse(File.ReadAllText(path));var json=doc.RootElement;
            var moduleScope=scenario is "both" or "modules"?json.GetProperty("settings"):json;
            var providerScope=scenario is "both" or "modules" or "providers"?json.GetProperty("settings"):json;
            var actual=moduleScope.GetProperty(scenario is "both" or "modules"?"host.modules":"modules").EnumerateArray().Select(item=>item.GetString()).ToArray();
            Assert.Contains("sync-core",actual);Assert.Contains("auth-ui",actual);
            var effective=providerScope.GetProperty(scenario is "both" or "modules" or "providers"?"host.providers":"providers");
            Assert.Equal("tasks-sqlite",effective.GetProperty("tasks.transfer-repository@1").GetString());
            Assert.Equal("tasks-core",effective.GetProperty("tasks.transfer@1").GetString());
            Assert.Equal("auth-http",effective.GetProperty("auth.transport@1").GetString());
            Assert.Equal("sync-http",effective.GetProperty("sync.transport@1").GetString());
            Assert.Equal(before,File.ReadAllText(path+".pre-migration"));
            // The migrated configuration must actually start: previously ModuleGraph.Resolve rejected it.
            await UntilCondition(() => !process.HasExited && !string.IsNullOrEmpty(process.MainWindowTitle), "Process exited after auth-sync migration");
        }
        finally{Cleanup(process);}
    }

    [Theory]
    [InlineData("top")]
    [InlineData("both")]
    [InlineData("modules")]
    [InlineData("providers")]
    public async Task Startup_repairs_capability_bindings_missing_from_an_upgraded_configuration(string scenario)
    {
        // The binding repair runs on every launch, independent of migration markers, so a module
        // upgrade that adds a Provided capability can no longer brick existing installations (the
        // 0.16.0 tasks.transfer-* incident). Every migration is already marked applied here: only
        // the always-on repair may fill the gaps.
        var root=NewRoot();var path=Path.Combine(root,"Config/appsettings.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var config=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(Root,"src/Mosaic.Desktop/appsettings.json")))!;
        var modules=config["modules"].Deserialize<string[]>()!;
        var providers=config["providers"].Deserialize<Dictionary<string,string>>()!;
        providers.Remove("tasks.transfer-repository@1");providers.Remove("tasks.transfer@1");providers.Remove("sync.api@1");
        config["providers"]=JsonSerializer.SerializeToElement(providers);
        var settings=new Dictionary<string,object>();
        if(scenario is "both" or "modules")settings["host.modules"]=modules;
        if(scenario is "both" or "providers")settings["host.providers"]=providers;
        config["settings"]=JsonSerializer.SerializeToElement(settings);
        File.WriteAllText(path,JsonSerializer.Serialize(config));var before=File.ReadAllText(path);
        using var process=StartExisting(root,hidden:true);
        try
        {
            await UntilCondition(()=>File.ReadAllText(path).Contains("tasks.transfer-repository@1",StringComparison.Ordinal),"Provider binding repair not persisted");
            using var doc=JsonDocument.Parse(File.ReadAllText(path));var json=doc.RootElement;
            var effective=scenario is "both" or "modules" or "providers"
                ? json.GetProperty("settings").GetProperty("host.providers")
                : json.GetProperty("providers");
            Assert.Equal("tasks-sqlite",effective.GetProperty("tasks.transfer-repository@1").GetString());
            Assert.Equal("tasks-core",effective.GetProperty("tasks.transfer@1").GetString());
            Assert.Equal("sync-core",effective.GetProperty("sync.api@1").GetString());
            Assert.Equal(before,File.ReadAllText(path+".pre-migration"));
            await UntilCondition(() => !process.HasExited && !string.IsNullOrEmpty(process.MainWindowTitle), "Process exited after provider binding repair");
        }
        finally{Cleanup(process);}
    }

    [Theory]
    [InlineData("top")]
    [InlineData("both")]
    [InlineData("modules")]
    [InlineData("providers")]
    public void Maintenance_migration_registers_the_page_and_its_bindings(string scenario)
    {
        // 0.16.6 and earlier have neither maintenance-ui nor the storage.relocation/app.maintenance
        // bindings. storage-sqlite now also provides storage.relocation@1, so an upgraded install
        // without the binding would fail ModuleGraph.Resolve at startup.
        var root=NewRoot();var path=Path.Combine(root,"Config/appsettings.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var config=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(Root,"src/Mosaic.Desktop/appsettings.json")))!;
        var modules=config["modules"].Deserialize<string[]>()!.Where(id=>id!="maintenance-ui").ToArray();
        var providers=config["providers"].Deserialize<Dictionary<string,string>>()!
            .Where(item=>item.Key is not ("storage.relocation@1" or "app.maintenance@1")).ToDictionary();
        config["modules"]=JsonSerializer.SerializeToElement(modules);
        config["providers"]=JsonSerializer.SerializeToElement(providers);
        config["appliedMigrations"]=JsonSerializer.SerializeToElement(new[]{"desktop-presence-v1","release-summary-v1","floating-windows-v1","auth-sync-v1"});
        var settings=new Dictionary<string,object>();
        if(scenario is "both" or "modules")settings["host.modules"]=modules;
        if(scenario is "both" or "providers")settings["host.providers"]=providers;
        config["settings"]=JsonSerializer.SerializeToElement(settings);
        File.WriteAllText(path,JsonSerializer.Serialize(config));

        var configuration=AppConfiguration.Load(new AppPaths(root));

        var effectiveModules=configuration.Settings!.TryGetValue("host.modules",out var moduleOverride)
            ? moduleOverride.Deserialize<string[]>()!
            : configuration.Modules;
        var effectiveProviders=configuration.Settings!.TryGetValue("host.providers",out var providerOverride)
            ? providerOverride.Deserialize<Dictionary<string,string>>()!
            : configuration.Providers;
        Assert.Contains("maintenance-ui",effectiveModules);
        Assert.Equal("storage-sqlite",effectiveProviders["storage.relocation@1"]);
        Assert.Equal("maintenance-ui",effectiveProviders["app.maintenance@1"]);
        Assert.Contains("maintenance-v1",configuration.AppliedMigrations!);
        using var document=JsonDocument.Parse(File.ReadAllText(path));var json=document.RootElement;
        var moduleScope=scenario is "both" or "modules"?json.GetProperty("settings").GetProperty("host.modules"):json.GetProperty("modules");
        var providerScope=scenario is "top"?json.GetProperty("providers"):json.GetProperty("settings").GetProperty("host.providers");
        Assert.Contains("maintenance-ui",moduleScope.EnumerateArray().Select(item=>item.GetString()));
        Assert.Equal("storage-sqlite",providerScope.GetProperty("storage.relocation@1").GetString());
        Assert.Equal("maintenance-ui",providerScope.GetProperty("app.maintenance@1").GetString());
        Assert.Contains("maintenance-v1",json.GetProperty("appliedMigrations").EnumerateArray().Select(item=>item.GetString()));
        Assert.True(File.Exists(path+".pre-migration"));
    }

    [Fact]
    public void Healthy_configuration_keeps_its_bindings_and_file_untouched()
    {
        var root=NewRoot();var path=Path.Combine(root,"Config/appsettings.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original=File.ReadAllText(Path.Combine(Root,"src/Mosaic.Desktop/appsettings.json"));
        File.WriteAllText(path,original);
        var configuration=AppConfiguration.Load(new AppPaths(root));
        Assert.Equal("tasks-sqlite",configuration.Providers["tasks.transfer-repository@1"]);
        Assert.Equal(original,File.ReadAllText(path));
        Assert.False(File.Exists(path+".pre-migration"));
    }

    [Fact]
    public void Repair_preserves_explicit_bindings_and_skips_disabled_modules()
    {
        var root=NewRoot();var path=Path.Combine(root,"Config/appsettings.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var config=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(Root,"src/Mosaic.Desktop/appsettings.json")))!;
        var modules=config["modules"].Deserialize<string[]>()!
            .Where(id=>id is not ("auth-http" or "auth-windows" or "auth-core" or "sync-http" or "sync-core" or "auth-ui")).ToArray();
        var providers=config["providers"].Deserialize<Dictionary<string,string>>()!
            .Where(item=>!item.Key.StartsWith("auth.",StringComparison.Ordinal)&&!item.Key.StartsWith("sync.",StringComparison.Ordinal)).ToDictionary();
        providers.Remove("tasks.changes@1");
        config["modules"]=JsonSerializer.SerializeToElement(modules);
        config["providers"]=JsonSerializer.SerializeToElement(providers);
        File.WriteAllText(path,JsonSerializer.Serialize(config));
        var configuration=AppConfiguration.Load(new AppPaths(root));
        Assert.Equal("tasks-core",configuration.Providers["tasks.changes@1"]);
        Assert.Equal("floating-windows-sqlite",configuration.Providers["floating.repository@1"]);
        // Capabilities of modules the user disabled must not gain orphan bindings.
        Assert.DoesNotContain("auth.transport@1",configuration.Providers.Keys);
        Assert.DoesNotContain("sync.api@1",configuration.Providers.Keys);
        using var doc=JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("tasks-core",doc.RootElement.GetProperty("providers").GetProperty("tasks.changes@1").GetString());
    }

    [Fact]
    public void Inspection_accepts_a_healthy_configuration_without_writing()
    {
        var root=NewRoot();var path=Path.Combine(root,"Config/appsettings.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original=File.ReadAllText(Path.Combine(Root,"src/Mosaic.Desktop/appsettings.json"));
        File.WriteAllText(path,original);

        var report=AppConfiguration.Inspect(new AppPaths(root));

        Assert.True(report.IsHealthy,string.Join(" | ",report.Issues));
        Assert.Empty(report.Issues);
        // The maintenance page relies on a check that never rewrites what it inspects.
        Assert.Equal(original,File.ReadAllText(path));
        Assert.False(File.Exists(path+".pre-migration"));
    }

    [Fact]
    public void Inspection_reports_a_binding_that_startup_repair_cannot_fix()
    {
        var root=NewRoot();var path=Path.Combine(root,"Config/appsettings.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var config=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(Root,"src/Mosaic.Desktop/appsettings.json")))!;
        var providers=config["providers"].Deserialize<Dictionary<string,string>>()!;
        providers["tasks.transfer@1"]="missing-provider";
        config["providers"]=JsonSerializer.SerializeToElement(providers);
        File.WriteAllText(path,JsonSerializer.Serialize(config));
        var before=File.ReadAllText(path);

        var report=AppConfiguration.Inspect(new AppPaths(root));

        Assert.False(report.IsHealthy);
        Assert.Contains(report.Issues,issue=>issue.Contains("tasks.transfer@1",StringComparison.Ordinal));
        Assert.Equal(before,File.ReadAllText(path));
    }

    [Fact]
    public void Inspection_reports_pending_migrations_and_invalid_configuration_files()
    {
        var root=NewRoot();var path=Path.Combine(root,"Config/appsettings.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        Assert.False(AppConfiguration.Inspect(new AppPaths(root)).IsHealthy); // no file at all

        var config=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(Root,"src/Mosaic.Desktop/appsettings.json")))!;
        config["appliedMigrations"]=JsonSerializer.SerializeToElement(Array.Empty<string>());
        File.WriteAllText(path,JsonSerializer.Serialize(config));
        var pending=AppConfiguration.Inspect(new AppPaths(root));
        Assert.False(pending.IsHealthy);
        Assert.Contains(pending.Issues,issue=>issue.Contains("maintenance-v1",StringComparison.Ordinal));

        File.WriteAllText(path,"{ not json");
        Assert.False(AppConfiguration.Inspect(new AppPaths(root)).IsHealthy);
    }

    [Theory]
    [InlineData("top")]
    [InlineData("both")]
    [InlineData("modules")]
    [InlineData("providers")]
    [InlineData("disabled")]
    [InlineData("empty")]
    public async Task Floating_configuration_migration_updates_effective_layer_once(string scenario)
    {
        var root=NewRoot();var path=Path.Combine(root,"Config/appsettings.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var config=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(Root,"src/Mosaic.Desktop/appsettings.json")))!;
        var modules=config["modules"].Deserialize<string[]>()!.Where(id=>id is not "floating-windows" and not "floating-windows-sqlite").ToArray();
        var providers=config["providers"].Deserialize<Dictionary<string,string>>()!.Where(item=>!item.Key.StartsWith("floating.",StringComparison.Ordinal)&&item.Key!="tasks.changes@1").ToDictionary();
        config["modules"]=JsonSerializer.SerializeToElement(scenario=="empty"?Array.Empty<string>():modules);
        config["providers"]=JsonSerializer.SerializeToElement(scenario=="empty"?new Dictionary<string,string>():providers);
        config["appliedMigrations"]=JsonSerializer.SerializeToElement(scenario=="disabled"?new[]{"desktop-presence-v1","release-summary-v1","floating-windows-v1"}:new[]{"desktop-presence-v1","release-summary-v1"});
        if(scenario=="disabled") {providers["tasks.changes@1"]="tasks-core";providers["floating.task-content@1"]="tasks-ui";providers["floating.detach-source@1"]="tasks-ui";config["providers"]=JsonSerializer.SerializeToElement(providers);}
        var settings=new Dictionary<string,object>();
        if(scenario is "both" or "modules")settings["host.modules"]=modules;
        if(scenario is "both" or "providers")settings["host.providers"]=providers;
        config["settings"]=JsonSerializer.SerializeToElement(settings);
        File.WriteAllText(path,JsonSerializer.Serialize(config));var before=File.ReadAllText(path);
        using var process=StartExisting(root,hidden:true);
        try
        {
            await UntilCondition(()=>File.ReadAllText(path).Contains("floating-windows-v1",StringComparison.Ordinal),"Floating migration not persisted");
            if(scenario=="disabled"){await Task.Delay(500);Assert.Equal(before,File.ReadAllText(path));return;}
            using var doc=JsonDocument.Parse(File.ReadAllText(path));var json=doc.RootElement;
            var moduleScope=scenario is "both" or "modules"?json.GetProperty("settings"):json;
            var providerScope=scenario is "both" or "modules" or "providers"?json.GetProperty("settings"):json;
            var actual=moduleScope.GetProperty(scenario is "both" or "modules"?"host.modules":"modules").EnumerateArray().Select(item=>item.GetString()).ToArray();
            if(scenario=="empty"){Assert.Empty(actual);Assert.False(File.Exists(Path.Combine(root,"Data/mosaic.sqlite")));return;}
            Assert.Contains("floating-windows",actual);Assert.Contains("floating-windows-sqlite",actual);
            Assert.Equal("tasks-core",providerScope.GetProperty(scenario is "both" or "modules" or "providers"?"host.providers":"providers").GetProperty("tasks.changes@1").GetString());
            Assert.Equal(before,File.ReadAllText(path+".pre-migration"));
        }
        finally{Cleanup(process);}
    }

    private sealed class FloatingDiagnostics:Mosaic.Contracts.IDiagnosticSink{public void Report(string correlationId,Exception exception){}}
    [Fact]
    public async Task Floating_published_restore_normal_exit_and_user_close_round_trip()
    {
        var root=NewRoot();var path=Path.Combine(root,"Data/mosaic.sqlite");var id=Guid.NewGuid();
        var reference=new Mosaic.FloatingWindows.Contracts.FloatingContentReference("tasks-ui",id.ToString("D"));
        await using(var storage=new Mosaic.Storage.Sqlite.SqliteModule(new(path),new FloatingDiagnostics()))
        {
            await storage.ActivateAsync();await using var tasks=new Mosaic.Tasks.Sqlite.TasksSqliteModule(storage.Session);await tasks.ActivateAsync();
            var now=DateTimeOffset.UtcNow;Assert.True((await tasks.Repository.InsertAsync(new(id,"悬浮恢复烟雾任务","可复制备注",false,now,now,1))).IsSuccess);
            await using var windows=new Mosaic.FloatingWindows.Sqlite.FloatingWindowsSqliteModule(storage.Session);await windows.ActivateAsync();
            Assert.True((await windows.Repository.SaveAsync(new(reference,true,Mosaic.FloatingWindows.Contracts.FloatingDisplayMode.Compact,320,320,360,"unplugged monitor",24,24,true,1),0)).IsSuccess);
        }
        AutomationElement? Main(Process process)=>AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ProcessIdProperty,process.Id)).Cast<AutomationElement>().FirstOrDefault(window=>window.Current.Name.StartsWith("Mosaic v",StringComparison.Ordinal));
        AutomationElement? Floating(Process process)=>AutomationElement.RootElement.FindFirst(TreeScope.Children,new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,process.Id),new PropertyCondition(AutomationElement.AutomationIdProperty,"FloatingWindow")));
        Process? process=null;
        try
        {
            process=Start(root);var main=await Until(()=>Main(process),"Missing main window");await Until(()=>Floating(process),"Missing restored window");
            ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));Assert.Equal(0,process.ExitCode);process.Dispose();
            process=Start(root);main=await Until(()=>Main(process),"Missing second main");var floating=await Until(()=>Floating(process),"Normal exit lost restoration");
            Click(Id(floating,"FloatingClose")!);await UntilCondition(()=>Floating(process) is null,"Floating close did not remove window");
            ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));Assert.Equal(0,process.ExitCode);process.Dispose();
            process=Start(root);main=await Until(()=>Main(process),"Missing third main");Assert.Null(Floating(process));
            ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));Assert.Equal(0,process.ExitCode);
        }
        finally{Cleanup(process);}
        await using(var storage=new Mosaic.Storage.Sqlite.SqliteModule(new(path),new FloatingDiagnostics()))
        {
            await storage.ActivateAsync();await using var tasks=new Mosaic.Tasks.Sqlite.TasksSqliteModule(storage.Session);await tasks.ActivateAsync();Assert.NotNull((await tasks.Repository.FindAsync(id)).Value);
            await using var windows=new Mosaic.FloatingWindows.Sqlite.FloatingWindowsSqliteModule(storage.Session);await windows.ActivateAsync();Assert.False(Assert.Single((await windows.Repository.LoadAsync()).Value).IsOpen);
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("backend")]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("duplicate")]
    [InlineData("disabled-provider")]
    [InlineData("traversal")]
    public async Task Disabled_and_invalid_configurations_do_not_create_task_views(string scenario)
    {
        var root = NewRoot(); Directory.CreateDirectory(Path.Combine(root, "Config"));
        var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(Root, "src/Mosaic.Desktop/appsettings.json")))!;
        if (scenario == "empty")
        {
            config["modules"] = JsonSerializer.SerializeToElement(Array.Empty<string>());
            config["providers"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>());
        }
        if (scenario == "backend")
        {
            var enabled = new[] { "storage-sqlite", "settings-sqlite", "settings-core", "tasks-sqlite", "tasks-core" };
            config["modules"] = JsonSerializer.SerializeToElement(enabled);
            config["providers"] = JsonSerializer.SerializeToElement(config["providers"].Deserialize<Dictionary<string, string>>()!.Where(item => enabled.Contains(item.Value)).ToDictionary());
        }
        if (scenario == "disabled-provider") config["modules"] = JsonSerializer.SerializeToElement(new[] { "storage-sqlite", "tasks-sqlite" });
        if (scenario == "missing") config["modules"] = JsonSerializer.SerializeToElement(new[] { "tasks-ui" });
        if (scenario == "traversal") config["storage"] = JsonSerializer.SerializeToElement(new { engine = "sqlite", location = "local-app-data", filename = "../escape.sqlite" });
        var configPath = Path.Combine(root, "Config/appsettings.json");
        File.WriteAllText(configPath, scenario == "invalid" ? "{bad json" : JsonSerializer.Serialize(config));
        if (scenario == "duplicate") File.WriteAllText(configPath, File.ReadAllText(configPath).Replace("\"configVersion\":1", "\"configVersion\":1,\"configVersion\":1", StringComparison.Ordinal));
        var before = File.ReadAllText(configPath);
        using var process = Start(root);
        try
        {
            var window = await Until(() => Window(process) is { } w && (scenario is "empty" or "backend" ? w.Current.Name.StartsWith("Mosaic v", StringComparison.Ordinal) : w.Current.Name == "Mosaic · 无法启动") ? w : null, "Expected window not found");
            Assert.Null(Id(window, "TaskTitle"));
            Assert.Equal(scenario == "backend", File.Exists(Path.Combine(root, "Data/mosaic.sqlite")));
            Assert.Equal(before, File.ReadAllText(configPath));
            await Close(process);
        }
        finally { Cleanup(process); }
    }
}
