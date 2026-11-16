using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;

namespace Mosaic.Desktop.SmokeTests;

public sealed class DesktopTests
{
    private static string Root
    {
        get { var p = new DirectoryInfo(AppContext.BaseDirectory); while (p is not null && !File.Exists(Path.Combine(p.FullName, "Mosaic.sln"))) p = p.Parent; return p!.FullName; }
    }
    private static string Executable => Environment.GetEnvironmentVariable("MOSAIC_SMOKE_EXE") ?? Path.Combine(Root, "artifacts/local-run/Mosaic.Desktop.exe");
    private static string NewRoot() { var path = Path.Combine(Root, "artifacts/smoke-data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
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
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false };
        info.ArgumentList.Add("--data-root"); info.ArgumentList.Add(dataRoot);
        return Process.Start(info)!;
    }
    private static async Task<T> Until<T>(Func<T?> get, string description) where T : class
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            try { if (get() is { } result) return result; } catch (ElementNotAvailableException) { }
            await Task.Delay(75);
        }
        throw new TimeoutException(description);
    }
    private static AutomationElement? Window(Process process) => AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
    private static AutomationElement? Id(AutomationElement parent, string id) => parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
    private static AutomationElement? Name(AutomationElement parent, string name) => parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
    private static void Click(AutomationElement element) => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    private static void Select(AutomationElement element) => ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
    private static void Text(AutomationElement element, string value) => ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    private static Task<AutomationElement> Control(Process process, string id) => Until(() => Window(process) is { } w ? Id(w, id) : null, "Missing control: " + id);
    private static Task<AutomationElement> Message(Process process, string text) => Until(() => Window(process) is { } w && Id(w, "TaskMessage") is { } m && m.Current.Name.Contains(text, StringComparison.Ordinal) ? m : null, "Missing message: " + text);
    private static async Task Close(Process process)
    {
        process.Refresh(); Assert.True(process.CloseMainWindow());
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, process.ExitCode);
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
            var item = await Until(() => Window(process) is { } w ? Name(w, "编辑后的任务") : null, "Persisted task not found");
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
            Click(Id(edge, "PresenceTrigger")!);
            var popup = await Until(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "PresencePopup"))), "Missing task overview");
            Assert.Null(Id(popup, "TaskTitle"));
            Click(Id(popup, "PresenceRestore")!);
            Assert.Equal("未保存的驻留草稿", ((ValuePattern)(await Control(process, "TaskTitle")).GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            using (var secondary = Start(root, presence: true)) await secondary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            // Clear only the unsaved test input so explicit exit can proceed without a prompt.
            Text(await Control(process, "TaskTitle"), "");
            process.Refresh(); Assert.True(process.CloseMainWindow());
            edge = await Until(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "PresenceEdge"))), "Missing second edge entry");
            Click(Id(edge, "PresenceTrigger")!);
            popup = await Until(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "PresencePopup"))), "Missing second overview");
            Click(Id(popup, "PresenceExit")!);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
        }
        finally { Cleanup(process); }
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
