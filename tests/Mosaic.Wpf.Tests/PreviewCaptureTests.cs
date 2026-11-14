using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Shell.Wpf;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Wpf;

namespace Mosaic.Wpf.Tests;

public sealed class PreviewCaptureTests
{
    [Fact]
    public Task Write_wide_and_narrow_previews_to_artifacts() => Sta.Run(async () =>
    {
        var root = FindRepositoryRoot();
        var output = Path.Combine(root, "artifacts", "ui-preview");
        Directory.CreateDirectory(output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(LoadTheme());

        await CaptureShell(root, output, 1220, 780, "shell-wide.png");
        await CaptureShell(root, output, 860, 700, "shell-narrow.png");
        await CaptureTasksOnly(root, output, 460, 640, "tasks-narrow.png");
    });

    private static async Task CaptureShell(string root, string output, double width, double height, string fileName)
    {
        var registry = new ContributionRegistry();
        var dialogs = new Dialogs();
        var module = new TasksUiModule(new(new Api(), new UiDispatcher(Dispatcher.CurrentDispatcher), dialogs, new Diagnostics()));
        await module.ActivateAsync();
        using var registration = registry.Register(module.Navigation, module.Views);
        var shell = new ShellWindow(registry, dialogs)
        {
            Width = width,
            Height = height,
            Left = -4000,
            Top = -4000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        await shell.InitializeAsync();
        shell.Show();
        shell.UpdateLayout();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        Render(shell, width, height, Path.Combine(output, fileName));
        await shell.DisposeAsync();
        registration.Dispose();
        shell.Close();
    }

    private static async Task CaptureTasksOnly(string root, string output, double width, double height, string fileName)
    {
        var dialogs = new Dialogs();
        var module = new TasksUiModule(new(new Api(), new UiDispatcher(Dispatcher.CurrentDispatcher), dialogs, new Diagnostics()));
        await module.ActivateAsync();
        var page = Assert.Single(module.Views).Factory.Create();
        await page.Controller.InitializeAsync();
        var resources = LoadTheme();
        var host = new Grid { Resources = resources };
        host.Children.Add(page.View);
        Arrange(host, width, height);
        Render(host, width, height, Path.Combine(output, fileName));
        await page.Controller.DisposeAsync();
    }

    private static void Render(FrameworkElement element, double width, double height, string path)
    {
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(width)),
            Math.Max(1, (int)Math.Ceiling(height)),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Arrange(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static ResourceDictionary LoadTheme()
    {
        var uri = new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative);
        return Assert.IsType<ResourceDictionary>(Application.LoadComponent(uri));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mosaic.sln"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate Mosaic.sln.");
    }

    private sealed class Dialogs : IUserDialogService
    {
        public Task<bool> ConfirmDeleteAsync(string title) => Task.FromResult(false);
        public Task<UnsavedChoice> AskUnsavedChangesAsync() => Task.FromResult(UnsavedChoice.Cancel);
        public Task ShowErrorAsync(string message) => Task.CompletedTask;
    }

    private sealed class Diagnostics : IDiagnosticSink
    {
        public void Report(string correlationId, Exception exception) { }
    }

    private sealed class Api : ITasksApi
    {
        private static readonly TaskItemDto First = new(
            Guid.NewGuid(),
            "完成产品方案初稿",
            "整理需求、画出主流程，并标注下一步需要确认的问题。",
            false,
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddDays(-1),
            3);
        private static readonly TaskItemDto Second = new(
            Guid.NewGuid(),
            "给任务页补上窄窗口布局",
            "验证 760 宽度下列表与编辑器上下排布，底部操作栏保持可见。",
            true,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow,
            1);
        public Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<TaskItemDto>.Success(First with { Title = request.Title, Notes = request.Notes }));
        public Task<OperationResult<TaskItemDto>> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<TaskItemDto>.Success(id == First.Id ? First : Second));
        public Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success(new[] { First, Second }));
        public Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<TaskItemDto>.Success(First with { Title = request.Title, Notes = request.Notes }));
        public Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<TaskItemDto>.Success((request.Id == First.Id ? First : Second) with { IsCompleted = request.IsCompleted }));
        public Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());
    }
}
