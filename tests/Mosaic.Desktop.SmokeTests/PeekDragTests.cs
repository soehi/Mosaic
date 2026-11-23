using System.Diagnostics;
using System.Windows.Automation;

namespace Mosaic.Desktop.SmokeTests;

public sealed partial class DesktopTests
{
    [Fact]
    public async Task Peek_real_pointer_detaches_multiple_tasks_and_dismisses_after_each_drop()
    {
        var root = NewRoot();
        var titles = new[] { "速览拖出任务一", "速览拖出任务二" };
        await using (var storage = new Mosaic.Storage.Sqlite.SqliteModule(new(Path.Combine(root, "Data/mosaic.sqlite")), new FloatingDiagnostics()))
        {
            await storage.ActivateAsync();
            await using var tasks = new Mosaic.Tasks.Sqlite.TasksSqliteModule(storage.Session);
            await tasks.ActivateAsync();
            var now = DateTimeOffset.UtcNow;
            foreach (var title in titles)
                Assert.True((await tasks.Repository.InsertAsync(new(Guid.NewGuid(), title, "", false, now, now, 1))).IsSuccess);
        }

        using var process = Start(root, presence: true);
        GetCursorPos(out var originalPointer);
        AutomationElement[] Surfaces(string automationId) => AutomationElement.RootElement.FindAll(TreeScope.Children,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))).Cast<AutomationElement>().ToArray();
        try
        {
            await Control(process, "TaskTitle");
            process.Refresh();
            Assert.True(process.CloseMainWindow());
            var edge = await Until(() => Surfaces("PresenceEdge").FirstOrDefault(), "Missing edge");
            var trigger = await Until(() => Id(edge, "PresenceTrigger"), "Missing trigger");
            for (var index = 0; index < titles.Length; index++)
            {
                Click(trigger);
                var popup = await Until(() => Surfaces("PresencePopup").FirstOrDefault(), "Missing overview");
                var row = await Until(() => popup.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                    .Cast<AutomationElement>().FirstOrDefault(button => button.Current.Name.StartsWith(titles[index] + "，", StringComparison.Ordinal)), "Missing task card: " + titles[index]);
                var bounds = row.Current.BoundingRectangle;
                var popupBounds = popup.Current.BoundingRectangle;
                var x = (int)(bounds.Left + bounds.Width / 2);
                var y = (int)(bounds.Top + bounds.Height / 2);
                var dropX = (int)popupBounds.Left - 100;
                await PointerGesture(x, y, dropX - x, 50);
                await Until(() => Surfaces("FloatingWindow").FirstOrDefault(window =>
                    Id(window, "FloatingTaskExpand")?.Current.Name.StartsWith(titles[index], StringComparison.Ordinal) == true), "Missing floating window: " + titles[index]);
                await UntilCondition(() => Surfaces("PresencePopup").Length == 0, "Overview did not close after drop");
                Assert.Equal(index + 1, Surfaces("FloatingWindow").Length);
            }
            var firstWindow = Surfaces("FloatingWindow").Single(window =>
                Id(window, "FloatingTaskExpand")?.Current.Name.StartsWith(titles[0], StringComparison.Ordinal) == true);
            Click(await Until(() => Id(firstWindow, "FloatingClose"), "Missing floating close button"));
            await UntilCondition(() => Surfaces("FloatingWindow").Length == 1, "Closing the first floating window should leave the second open");
            Assert.StartsWith(titles[1], Id(Surfaces("FloatingWindow").Single(), "FloatingTaskExpand")!.Current.Name);
            Click(trigger);
            var reopened = await Until(() => Surfaces("PresencePopup").FirstOrDefault(), "Missing reopened overview");
            Click(await Until(() => Id(reopened, "PresenceMore"), "Missing more control"));
            Click(await Until(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "PresenceExit"))), "Missing exit"));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            SetCursorPos(originalPointer.X, originalPointer.Y);
            Cleanup(process);
        }
    }
}
