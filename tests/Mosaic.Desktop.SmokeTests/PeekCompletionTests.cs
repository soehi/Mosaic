using System.Diagnostics;
using System.Windows.Automation;

namespace Mosaic.Desktop.SmokeTests;

public sealed partial class DesktopTests
{
    [Fact]
    public async Task Peek_completion_and_filter_use_real_persisted_task_state()
    {
        var root = NewRoot(); var id = Guid.NewGuid();
        await using (var storage = new Mosaic.Storage.Sqlite.SqliteModule(new(Path.Combine(root, "Data/mosaic.sqlite")), new FloatingDiagnostics()))
        {
            await storage.ActivateAsync();
            await using var tasks = new Mosaic.Tasks.Sqlite.TasksSqliteModule(storage.Session); await tasks.ActivateAsync();
            var now = DateTimeOffset.UtcNow;
            Assert.True((await tasks.Repository.InsertAsync(new(id, "速览完成回归", "", false, now, now, 1))).IsSuccess);
            await using var settings = new Mosaic.Settings.Sqlite.SettingsSqliteModule(new(storage.Session)); await settings.ActivateAsync();
            Assert.True((await settings.Repository.SaveAsync(new Dictionary<string, object?> { ["desktop.popup.autoHide"] = false, ["updates.summaryTask.enabled"] = false })).IsSuccess);
        }
        AutomationElement? Surface(Process app, string name) => AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id), new PropertyCondition(AutomationElement.AutomationIdProperty, name)));
        async Task ParkAsync(Process app)
        {
            await Control(app, "TaskTitle");
            app.Refresh();
            Assert.True(app.CloseMainWindow());
        }
        async Task<AutomationElement> OpenOverviewAsync(Process app)
        {
            var edge = await Until(() => Surface(app, "PresenceEdge"), "Missing edge");
            Click(await Until(() => Id(edge, "PresenceTrigger"), "Missing trigger"));
            return await Until(() => Surface(app, "PresencePopup"), "Missing overview");
        }

        using var process = Start(root, presence: true);
        try
        {
            await ParkAsync(process);
            var popup = await OpenOverviewAsync(process);
            Select(await Until(() => Id(popup, "PeekFilterOpen"), "Missing Open filter"));
            var complete = await Until(() => Id(popup, "PeekComplete"), "Missing completion button");
            Assert.Contains("标记完成", complete.Current.Name); Click(complete);
            await UntilCondition(() => Id(popup, "PeekComplete") is null && Name(popup, "没有符合筛选的任务") is not null, "Completed row remains in Open filter");
            Select(await Until(() => Id(popup, "PeekFilterCompleted"), "Missing Completed filter"));
            var reopen = await Until(() => Id(popup, "PeekComplete"), "Completed task did not appear");
            Assert.Contains("恢复未完成", reopen.Current.Name); Click(reopen);
            await UntilCondition(() => Id(popup, "PeekComplete") is null && Name(popup, "没有符合筛选的任务") is not null, "Reopened row remains in Completed filter");
            Select(await Until(() => Id(popup, "PeekFilterOpen"), "Missing Open filter"));
            await Until(() => Id(popup, "PeekComplete"), "Reopened task did not appear");
            Click(await Until(() => Id(popup, "PresenceDismiss"), "Missing dismiss control"));
            await UntilCondition(() => Surface(process, "PresencePopup") is null, "Overview did not close");
            popup = await OpenOverviewAsync(process);
            await VerifyOpenFilterAsync(popup);
            Click(await Until(() => Id(popup, "PresenceMore"), "Missing more control"));
            Click(await Until(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "PresenceExit")), "Missing exit"));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(0, process.ExitCode);
        }
        finally { Cleanup(process); }

        using (var restarted = Start(root, presence: true))
        {
            try
            {
                await ParkAsync(restarted);
                await VerifyOpenFilterAsync(await OpenOverviewAsync(restarted));
            }
            finally { Cleanup(restarted); }
        }

        static Task VerifyOpenFilterAsync(AutomationElement overview) => UntilCondition(() =>
            Id(overview, "PeekFilterOpen")?.GetCurrentPropertyValue(SelectionItemPattern.IsSelectedProperty) is true,
            "Overview did not restore the Open filter");

        await using var verifyStorage = new Mosaic.Storage.Sqlite.SqliteModule(new(Path.Combine(root, "Data/mosaic.sqlite")), new FloatingDiagnostics());
        await verifyStorage.ActivateAsync();
        await using var verifyTasks = new Mosaic.Tasks.Sqlite.TasksSqliteModule(verifyStorage.Session); await verifyTasks.ActivateAsync();
        var saved = await verifyTasks.Repository.FindAsync(id);
        Assert.True(saved.IsSuccess); Assert.NotNull(saved.Value); Assert.False(saved.Value.IsCompleted); Assert.Equal(3, saved.Value.Revision);
    }
}
