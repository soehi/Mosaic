using System.Diagnostics;
using System.Windows.Automation;
using Mosaic.FloatingWindows.Contracts;
using Mosaic.Settings.Sqlite;
using Mosaic.Storage.Sqlite;
using Mosaic.Tasks.Sqlite;

namespace Mosaic.Desktop.SmokeTests;

public sealed partial class DesktopTests
{
    [Fact]
    public async Task Published_floating_notes_default_to_read_only_and_save_when_enabled()
    {
        const string setting = "tasks.floating.allowNotesEditing";
        var root = NewRoot();
        var path = Path.Combine(root, "Data/mosaic.sqlite");
        var id = Guid.NewGuid();
        var reference = new FloatingContentReference("tasks-ui", id.ToString("D"));
        await using (var storage = new SqliteModule(new(path), new FloatingDiagnostics()))
        {
            await storage.ActivateAsync();
            await using var tasks = new TasksSqliteModule(storage.Session);
            await tasks.ActivateAsync();
            var now = DateTimeOffset.UtcNow;
            Assert.True((await tasks.Repository.InsertAsync(new(id, "浮窗备注验收", "原始备注", false, now, now, 1))).IsSuccess);
            await using var windows = new Mosaic.FloatingWindows.Sqlite.FloatingWindowsSqliteModule(storage.Session);
            await windows.ActivateAsync();
            Assert.True((await windows.Repository.SaveAsync(new(reference, true, FloatingDisplayMode.Expanded, 320, 400, 360, "missing monitor", 200, 200, true, 1), 0)).IsSuccess);
        }

        Process? process = null;
        AutomationElement? Floating() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, process!.Id),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "FloatingWindow")));
        async Task Exit()
        {
            var shell = await Until(() => AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, process!.Id)).Cast<AutomationElement>()
                .FirstOrDefault(element => element.Current.Name.StartsWith("Mosaic v", StringComparison.Ordinal)), "Missing Mosaic shell");
            ((WindowPattern)shell.GetCurrentPattern(WindowPattern.Pattern)).Close();
            await process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
            process.Dispose();
            process = null;
        }
        try
        {
            process = Start(root);
            var readOnly = await Until(() => Floating() is { } window ? Id(window, "FloatingNotes") : null, "Missing read-only notes");
            Assert.True(NotesValue(readOnly).IsReadOnly);
            Assert.Equal("原始备注", NotesValue(readOnly).Value);
            await Exit();

            await using (var storage = new SqliteModule(new(path), new FloatingDiagnostics()))
            {
                await storage.ActivateAsync();
                await using var settings = new SettingsSqliteModule(new(storage.Session));
                await settings.ActivateAsync();
                Assert.True((await settings.Repository.SaveAsync(new Dictionary<string, object?> { [setting] = true })).IsSuccess);
            }
            process = Start(root);
            var floating = await Until(Floating, "Missing restored floating window");
            Click(await Until(() => Id(floating, "FloatingNotesEdit"), "Missing editing action after restart"));
            var editor = await Until(() => Id(floating, "FloatingNotes") is { } notes && !NotesValue(notes).IsReadOnly ? notes : null, "Notes did not enter editing mode");
            Text(editor, "已保存的多行浮窗备注\r\n第二行");
            Click(await Until(() => Id(floating, "FloatingNotesSave"), "Missing save action"));
            await UntilCondition(() => NotesValue(editor).IsReadOnly, "Notes did not return to read-only state");
            await Exit();

            await using var check = new SqliteModule(new(path), new FloatingDiagnostics());
            await check.ActivateAsync();
            await using var savedTasks = new TasksSqliteModule(check.Session);
            await savedTasks.ActivateAsync();
            var item = (await savedTasks.Repository.FindAsync(id)).Value!;
            Assert.Equal("浮窗备注验收", item.Title);
            Assert.Equal("已保存的多行浮窗备注\n第二行", item.Notes.Replace("\r\n", "\n"));
            Assert.Equal(2, item.Revision);
        }
        finally { Cleanup(process); }
    }

    private static ValuePattern.ValuePatternInformation NotesValue(AutomationElement element) =>
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current;
}
