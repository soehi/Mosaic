using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Settings.Contracts;
using Mosaic.Shell.Wpf;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Wpf;
using Mosaic.Tasks.Wpf.Views;

namespace Mosaic.Wpf.Tests;

public sealed class ResponsiveLayoutTests
{
    [Fact]
    public Task Task_page_keeps_both_panes_on_the_same_view_instances() => Sta.Run(async () =>
    {
        var api = new Api();
        var page = await CreateTaskPage(api, new Dialogs());
        var view = page.View;
        view.Resources.MergedDictionaries.Add(LoadTheme());
        await page.Controller.InitializeAsync();

        var model = view.DataContext!;
        var editorModel = Get<object>(model, "Editor");
        var list = Assert.IsType<Grid>(view.FindName("TaskListPane"));
        var editor = Assert.IsType<TaskEditorView>(view.FindName("TaskEditorPane"));
        var compactNavigation = Assert.IsType<Border>(editor.FindName("CompactNavigationBar"));
        var actionBar = Assert.IsType<Border>(editor.FindName("EditorActionBar"));
        var actionGrid = Assert.IsType<Grid>(editor.FindName("EditorActionGrid"));
        var deleteButton = Assert.IsType<Button>(editor.FindName("DeleteButton"));

        Arrange(view, 920, 780);
        Assert.Equal(Visibility.Visible, list.Visibility);
        Assert.Equal(Visibility.Visible, editor.Visibility);
        Assert.Equal(2, Grid.GetColumn(editor));
        Assert.Equal(Visibility.Collapsed, compactNavigation.Visibility);

        Arrange(view, 919, 780);
        Assert.Equal(Visibility.Visible, list.Visibility);
        Assert.Equal(Visibility.Collapsed, editor.Visibility);
        Assert.Equal(0, Grid.GetColumn(editor));
        Assert.Equal(2, Grid.GetRow(editor));
        Assert.Equal(Visibility.Collapsed, compactNavigation.Visibility);

        Arrange(view, 839, 620);
        Assert.Equal(Visibility.Visible, list.Visibility);
        Assert.Equal(Visibility.Collapsed, editor.Visibility);

        await Get<IAsyncRelayCommand<TaskItemDto>>(model, "SelectCommand").ExecuteAsync(api.Item);
        view.UpdateLayout();
        Assert.True(Get<bool>(model, "IsEditorOpen"));
        Assert.Equal(Visibility.Collapsed, list.Visibility);
        Assert.Equal(Visibility.Visible, editor.Visibility);
        Assert.Equal(Visibility.Visible, compactNavigation.Visibility);
        Assert.Equal(0, Grid.GetColumn(editor));
        Assert.Equal(2, Grid.GetRow(editor));

        Set(editorModel, "Title", "断点往返后仍未保存的标题");
        Arrange(view, 1220, 780);
        Assert.Equal(Visibility.Visible, list.Visibility);
        Assert.Equal(Visibility.Visible, editor.Visibility);
        Assert.Equal(Visibility.Collapsed, compactNavigation.Visibility);
        Assert.Equal("断点往返后仍未保存的标题", Get<string>(editorModel, "Title"));

        Arrange(view, 760, 620);
        Assert.Equal(Visibility.Collapsed, list.Visibility);
        Assert.Equal(Visibility.Visible, editor.Visibility);
        Assert.Equal(Visibility.Visible, compactNavigation.Visibility);
        Assert.Equal(0, Grid.GetColumn(editor));
        Assert.Equal(2, Grid.GetRow(editor));
        Assert.Equal("断点往返后仍未保存的标题", Get<string>(editorModel, "Title"));
        Assert.True(actionBar.ActualHeight > 0);
        Assert.Equal(1, Grid.GetRow(deleteButton));
        Assert.Equal(actionGrid.ColumnDefinitions.Count, Grid.GetColumnSpan(deleteButton));
        Assert.True(deleteButton.MinHeight >= 40);
        Assert.True(actionBar.TranslatePoint(new Point(0, actionBar.ActualHeight), editor).Y <= editor.ActualHeight + 0.5);

        await page.Controller.DisposeAsync();
    });

    [Fact]
    public Task Back_to_list_honors_cancel_discard_and_save_choices() => Sta.Run(async () =>
    {
        var api = new Api();
        var dialogs = new Dialogs();
        var page = await CreateTaskPage(api, dialogs);
        await page.Controller.InitializeAsync();
        var model = page.View.DataContext!;
        var editorModel = Get<object>(model, "Editor");

        await Get<IAsyncRelayCommand<TaskItemDto>>(model, "SelectCommand").ExecuteAsync(api.Item);
        Set(editorModel, "Title", "未保存标题");

        dialogs.Choice = UnsavedChoice.Cancel;
        await Get<IAsyncRelayCommand>(model, "BackToListCommand").ExecuteAsync(null);
        Assert.True(Get<bool>(model, "IsEditorOpen"));
        Assert.Equal("未保存标题", Get<string>(editorModel, "Title"));

        dialogs.Choice = UnsavedChoice.Discard;
        await Get<IAsyncRelayCommand>(model, "BackToListCommand").ExecuteAsync(null);
        Assert.False(Get<bool>(model, "IsEditorOpen"));
        Assert.Equal(api.Item.Title, Get<string>(editorModel, "Title"));
        Assert.False(Get<bool>(editorModel, "IsDirty"));

        await Get<IAsyncRelayCommand<TaskItemDto>>(model, "SelectCommand").ExecuteAsync(api.Item);
        Set(editorModel, "Title", "保存后返回");
        dialogs.Choice = UnsavedChoice.Save;
        await Get<IAsyncRelayCommand>(model, "BackToListCommand").ExecuteAsync(null);
        Assert.False(Get<bool>(model, "IsEditorOpen"));
        Assert.Equal(1, api.Updates);
        Assert.False(Get<bool>(editorModel, "IsDirty"));

        await page.Controller.DisposeAsync();
    });

    [Fact]
    public Task Shell_sidebar_uses_the_1180_rail_and_1200_expand_hysteresis() => Sta.Run(async () =>
    {
        var registry = new ContributionRegistry();
        var dialogs = new Dialogs();
        var module = new TasksUiModule(new(new Api(), new UiDispatcher(Dispatcher.CurrentDispatcher), dialogs, new Diagnostics()));
        await module.ActivateAsync();
        using var registration = registry.Register(module.Navigation, module.Views);

        var shell = new ShellWindow(registry, dialogs)
        {
            Left = -4000,
            Top = -4000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        shell.Resources.MergedDictionaries.Add(LoadTheme());
        await shell.InitializeAsync();
        shell.Show();

        ResizeShell(shell, 1220, 780);
        Assert.False(shell.IsSidebarCompact);
        Assert.Equal(190, Assert.IsType<ColumnDefinition>(shell.FindName("SidebarColumn")).Width.Value);

        ResizeShell(shell, 1180, 780);
        Assert.False(shell.IsSidebarCompact);

        ResizeShell(shell, 1179, 780);
        Assert.True(shell.IsSidebarCompact);
        Assert.Equal(64, Assert.IsType<ColumnDefinition>(shell.FindName("SidebarColumn")).Width.Value);
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        shell.UpdateLayout();

        var navigationButtons = FindVisualChildren<Button>(Assert.IsType<ItemsControl>(shell.FindName("NavigationItems"))).ToArray();
        var navigationButton = Assert.Single(navigationButtons, button => Equals(button.ToolTip, "任务"));
        Assert.Equal("任务", navigationButton.ToolTip);
        Assert.Equal("任务", System.Windows.Automation.AutomationProperties.GetName(navigationButton));
        var navigationLabels = FindVisualChildren<TextBlock>(shell)
            .Where(text => text.Name == "NavigationLabel" && text.Text is "任务" or "已归档任务").ToArray();
        Assert.Equal(2, navigationLabels.Length);
        Assert.All(navigationLabels, text => Assert.Equal(Visibility.Collapsed, text.Visibility));

        ResizeShell(shell, 839, 620);
        Assert.True(shell.IsSidebarCompact);
        Assert.Equal(64, Assert.IsType<ColumnDefinition>(shell.FindName("SidebarColumn")).Width.Value);

        ResizeShell(shell, 1199, 780);
        Assert.True(shell.IsSidebarCompact);

        ResizeShell(shell, 1200, 780);
        Assert.False(shell.IsSidebarCompact);
        Assert.Equal(190, Assert.IsType<ColumnDefinition>(shell.FindName("SidebarColumn")).Width.Value);

        ResizeShell(shell, 760, 620);
        Assert.True(shell.IsSidebarCompact);
        Assert.Equal(64, Assert.IsType<ColumnDefinition>(shell.FindName("SidebarColumn")).Width.Value);

        await shell.DisposeAsync();
        shell.Close();
    });

    [Fact]
    public Task Sidebar_drag_resizes_with_limits_and_navigation_borders_remain_inside_the_sidebar() => Sta.Run(async () =>
    {
        var registry = new ContributionRegistry();
        var dialogs = new Dialogs();
        var module = new TasksUiModule(new(new Api(), new UiDispatcher(Dispatcher.CurrentDispatcher), dialogs, new Diagnostics()));
        await module.ActivateAsync();
        using var registration = registry.Register(module.Navigation, module.Views);
        var shell = new ShellWindow(registry, dialogs) { ShowInTaskbar = false, ShowActivated = false };
        shell.Resources.MergedDictionaries.Add(LoadTheme());
        try
        {
            await shell.InitializeAsync();
            shell.Show();
            ResizeShell(shell, 1220, 780);
            var column = Assert.IsType<ColumnDefinition>(shell.FindName("SidebarColumn"));
            var handle = Assert.IsType<Thumb>(shell.FindName("SidebarResizeHandle"));
            var sidebar = Assert.IsType<Border>(shell.FindName("Sidebar"));
            var navigation = Assert.IsType<ItemsControl>(shell.FindName("NavigationItems"));
            void AssertInside()
            {
                shell.UpdateLayout();
                foreach (var button in FindVisualChildren<Button>(navigation))
                {
                    var position = button.TranslatePoint(new Point(), sidebar);
                    Assert.True(position.X >= 0);
                    Assert.True(position.X + button.ActualWidth <= sidebar.ActualWidth - 1 + 0.5);
                }
            }
            void Drag(double change, bool cancel = false)
            {
                handle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                handle.RaiseEvent(new DragDeltaEventArgs(change, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                handle.RaiseEvent(new DragCompletedEventArgs(change, 0, cancel) { RoutedEvent = Thumb.DragCompletedEvent });
                shell.UpdateLayout();
            }
            AssertInside();
            Drag(70);
            Assert.Equal(260, column.Width.Value);
            AssertInside();
            Drag(-1000);
            Assert.Equal(160, column.Width.Value);
            AssertInside();
            Drag(1000);
            Assert.Equal(320, column.Width.Value);
            AssertInside();
            Drag(-80, cancel: true);
            Assert.Equal(320, column.Width.Value);
            ResizeShell(shell, 1100, 720);
            Assert.True(shell.IsSidebarCompact);
            AssertInside();
            Drag(150);
            Assert.False(shell.IsSidebarCompact);
            Assert.Equal(214, column.Width.Value);
            AssertInside();
            ResizeShell(shell, 1179, 720);
            Assert.True(shell.IsSidebarCompact);
            ResizeShell(shell, 1220, 780);
            Assert.Equal(214, column.Width.Value);
            AssertInside();
        }
        finally
        {
            await shell.DisposeAsync();
            shell.Close();
            await module.DisposeAsync();
        }
    });

    [Fact]
    public Task Shell_consumes_window_and_layout_settings_from_the_settings_api() => Sta.Run(async () =>
    {
        var registry = new ContributionRegistry();
        var dialogs = new Dialogs();
        var module = new TasksUiModule(new(new Api(), new UiDispatcher(Dispatcher.CurrentDispatcher), dialogs, new Diagnostics()));
        await module.ActivateAsync();
        using var registration = registry.Register(module.Navigation, module.Views);

        var settings = new LayoutSettings();
        var shell = new ShellWindow(registry, dialogs, settings)
        {
            Left = -4000,
            Top = -4000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        shell.Resources.MergedDictionaries.Add(LoadTheme());
        await shell.InitializeAsync();

        Assert.Equal(1000, shell.Width);
        Assert.Equal(700, shell.Height);
        Assert.Equal(800, shell.MinWidth);
        Assert.Equal(600, shell.MinHeight);
        var pageContent = Assert.IsType<ContentControl>(shell.FindName("PageContent"));
        Assert.Equal(new Thickness(40, 36, 32, 32), pageContent.Margin);

        ResizeShell(shell, 950, 700);
        Assert.False(shell.IsSidebarCompact);
        Assert.Equal(200, Assert.IsType<ColumnDefinition>(shell.FindName("SidebarColumn")).Width.Value);

        ResizeShell(shell, 899, 700);
        Assert.True(shell.IsSidebarCompact);
        Assert.Equal(56, Assert.IsType<ColumnDefinition>(shell.FindName("SidebarColumn")).Width.Value);

        ResizeShell(shell, 919, 700);
        Assert.True(shell.IsSidebarCompact);
        Assert.Equal(56, Assert.IsType<ColumnDefinition>(shell.FindName("SidebarColumn")).Width.Value);

        ResizeShell(shell, 920, 700);
        Assert.False(shell.IsSidebarCompact);
        Assert.Equal(200, Assert.IsType<ColumnDefinition>(shell.FindName("SidebarColumn")).Width.Value);

        await shell.DisposeAsync();
        shell.Close();
    });

    [Fact]
    public Task Over_compressed_task_views_keep_actions_and_text_operable() => Sta.Run(async () =>
    {
        var api = new Api();
        var page = await CreateTaskPage(api, new Dialogs());
        var view = page.View;
        view.Resources.MergedDictionaries.Add(LoadTheme());
        await page.Controller.InitializeAsync();

        var model = view.DataContext!;
        var list = Assert.IsType<Grid>(view.FindName("TaskListPane"));
        var editor = Assert.IsType<TaskEditorView>(view.FindName("TaskEditorPane"));
        var form = Assert.IsType<ScrollViewer>(editor.FindName("FormScrollViewer"));
        var actionBar = Assert.IsType<Border>(editor.FindName("EditorActionBar"));
        var notes = Assert.IsType<TextBox>(editor.FindName("NotesTextBox"));
        var toggle = Assert.IsType<Button>(editor.FindName("ToggleButton"));
        var reload = Assert.IsType<Button>(editor.FindName("ReloadButton"));
        var delete = Assert.IsType<Button>(editor.FindName("DeleteButton"));

        Arrange(view, 420, 480);
        Assert.True(view.ActualWidth <= 420);
        Assert.Equal(Visibility.Visible, list.Visibility);
        Assert.Equal(Visibility.Collapsed, editor.Visibility);
        var newTask = Assert.IsType<Button>(view.FindName("NewTaskButton"));
        Assert.True(newTask.ActualWidth >= 96);
        Assert.True(newTask.ActualHeight >= 40);

        await Get<IAsyncRelayCommand<TaskItemDto>>(model, "SelectCommand").ExecuteAsync(api.Item);
        Arrange(view, 560, 480);
        Assert.Equal(Visibility.Collapsed, list.Visibility);
        Assert.Equal(Visibility.Visible, editor.Visibility);
        Assert.True(toggle.ActualWidth >= 96);
        Assert.True(toggle.ActualHeight >= 40);
        Assert.True(reload.ActualWidth >= 96);
        Assert.True(reload.ActualHeight >= 40);
        Assert.Equal(1, Grid.GetRow(delete));
        Assert.True(delete.ActualHeight >= 40);
        Assert.True(notes.ActualHeight >= 128);
        Assert.True(form.ExtentHeight >= form.ViewportHeight);
        Assert.True(actionBar.TranslatePoint(new Point(0, actionBar.ActualHeight), editor).Y <= editor.ActualHeight + 0.5);

        Arrange(view, 480, 480);
        Assert.Equal(Visibility.Collapsed, list.Visibility);
        Assert.Equal(Visibility.Visible, editor.Visibility);
        Assert.True(toggle.ActualWidth >= 96);
        Assert.True(delete.ActualHeight >= 40);

        await page.Controller.DisposeAsync();
    });

    private static async Task<ViewPage> CreateTaskPage(ITasksApi api, IUserDialogService dialogs)
    {
        var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), dialogs, new Diagnostics()));
        await module.ActivateAsync();
        return Assert.Single(module.Views, view => view.Id == "tasks.list").Factory.Create();
    }

    private static void ResizeShell(ShellWindow shell, double width, double height)
    {
        shell.Width = width;
        shell.Height = height;
        shell.Dispatcher.Invoke(() =>
        {
            typeof(ShellWindow)
                .GetMethod("UpdateSidebarLayout", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(shell, [width]);
        });
        shell.UpdateLayout();
    }

    [Fact]
    public Task Shell_starts_on_tasks_even_when_the_account_choice_is_still_open() => Sta.Run(async () =>
    {
        var registry = new ContributionRegistry();
        var dialogs = new Dialogs();
        var module = new TasksUiModule(new(new Api(), new UiDispatcher(Dispatcher.CurrentDispatcher), dialogs, new Diagnostics()));
        await module.ActivateAsync();
        using var registration = registry.Register(module.Navigation, module.Views);

        // 手工制造“未完成首次账户选择”的状态：启动路径不应再被 settings.home 劫持。
        var settingsPages = new SettingsPageRegistry();
        var shell = new ShellWindow(registry, dialogs, null, settingsPages)
        {
            Left = -4000,
            Top = -4000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        shell.Resources.MergedDictionaries.Add(LoadTheme());

        await shell.InitializeAsync();

        Assert.Equal("tasks.navigation", Get<string>(shell.DataContext!, "CurrentNavigationId"));
        Assert.False(settingsPages.IsInitialChoiceActive);
        await shell.DisposeAsync();
    });

    private static void Arrange(FrameworkElement element, double width, double height)
    {
        var size = new Size(width, height);
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
    }

    private static ResourceDictionary LoadTheme()
    {
        var uri = new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative);
        return Assert.IsType<ResourceDictionary>(Application.LoadComponent(uri));
    }

    private static T Get<T>(object model, string property) =>
        (T)model.GetType().GetProperty(property)!.GetValue(model)!;

    private static void Set(object model, string property, object value) =>
        model.GetType().GetProperty(property)!.SetValue(model, value);

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private sealed class Dialogs : IUserDialogService
    {
        public UnsavedChoice Choice { get; set; } = UnsavedChoice.Cancel;
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteAsync(string title) => Task.FromResult(false);
        public Task<UnsavedChoice> AskUnsavedChangesAsync() => Task.FromResult(Choice);
        public Task ShowErrorAsync(string message) => Task.CompletedTask;
    }

    private sealed class Diagnostics : IDiagnosticSink
    {
        public void Report(string correlationId, Exception exception) { }
    }

    private sealed class LayoutSettings : ISettingsApi
    {
        public Task<OperationResult<IReadOnlyList<SettingDefinition>>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<SettingDefinition>>.Success([]));

        public Task<OperationResult<IReadOnlyDictionary<string, object?>>> GetValuesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, object?>>.Success(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["startup.width"] = 1000,
                    ["startup.height"] = 700,
                    ["shell.window.minWidth"] = 800,
                    ["shell.window.minHeight"] = 600,
                    ["shell.sidebar.collapseBelowWidth"] = 900,
                    ["shell.sidebar.expandAtWidth"] = 920,
                    ["shell.sidebar.fullWidth"] = 200,
                    ["shell.sidebar.railWidth"] = 56,
                    ["shell.page.marginLeft"] = 40,
                    ["shell.page.marginTop"] = 36,
                    ["shell.page.marginRight"] = 32,
                    ["shell.page.marginBottom"] = 32
                }));

        public Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
            IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, OperationError>>.Success(new Dictionary<string, OperationError>()));

        public Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());
        public Task<OperationResult> ResetAsync(IReadOnlyList<string> definitionIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());
    }

    private sealed class Api : ITasksApi
    {
        public TaskItemDto Item { get; private set; } = new(
            Guid.NewGuid(),
            "完成产品方案初稿",
            "整理需求并记录下一步。",
            false,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow,
            3);
        public int Updates { get; private set; }

        public Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<TaskItemDto>.Success(Item with { Title = request.Title, Notes = request.Notes }));

        public Task<OperationResult<TaskItemDto>> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<TaskItemDto>.Success(Item));

        public Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([Item]));

        public Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default)
        {
            Updates++;
            Item = Item with { Title = request.Title, Notes = request.Notes, Revision = request.ExpectedRevision + 1 };
            return Task.FromResult(OperationResult<TaskItemDto>.Success(Item));
        }

        public Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<TaskItemDto>.Success(Item with { IsCompleted = request.IsCompleted }));

        public Task<OperationResult<TaskItemDto>> SetArchivedAsync(SetTaskArchivedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult<TaskConstraintsDto>> GetConstraintsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<TaskConstraintsDto>.Success(new(200, 20_000, true, false, 1, 50, 10)));
    }
}
