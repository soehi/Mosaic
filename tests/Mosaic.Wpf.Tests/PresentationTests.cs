using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Shell.Wpf;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Wpf;

namespace Mosaic.Wpf.Tests;

public sealed class PresentationTests
{
    private sealed class Dialogs : IUserDialogService
    {
        internal UnsavedChoice Choice = UnsavedChoice.Cancel;
        internal bool Delete = false;
        public Task<bool> ConfirmDeleteAsync(string title) => Task.FromResult(Delete);
        public Task<UnsavedChoice> AskUnsavedChangesAsync() => Task.FromResult(Choice);
        public Task ShowErrorAsync(string message) => Task.CompletedTask;
    }
    private sealed class Api : ITasksApi
    {
        internal Func<TaskQuery, Task<OperationResult<IReadOnlyList<TaskItemDto>>>> Query = _ => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([]));
        internal OperationError? SaveError;
        internal int Creates;
        internal int Deletes;
        internal Func<CreateTaskRequest, Task<OperationResult<TaskItemDto>>>? Creator;
        internal TaskItemDto Item = new(Guid.NewGuid(), "Existing", "", false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
        public Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default) { Creates++; return Creator?.Invoke(request) ?? Task.FromResult(SaveError is null ? OperationResult<TaskItemDto>.Success(Item with { Title = request.Title, Notes = request.Notes }) : OperationResult<TaskItemDto>.Failure(SaveError)); }
        public Task<OperationResult<TaskItemDto>> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult<TaskItemDto>.Success(Item));
        public Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default) => Query(query);
        public Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default) => Task.FromResult(SaveError is null ? OperationResult<TaskItemDto>.Success(Item with { Title = request.Title, Revision = request.ExpectedRevision + 1 }) : OperationResult<TaskItemDto>.Failure(SaveError));
        public Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult<TaskItemDto>.Success(Item with { IsCompleted = request.IsCompleted }));
        public Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) { Deletes++; return Task.FromResult(OperationResult.Success()); }
    }
    private sealed class Diagnostics : IDiagnosticSink
    {
        internal List<(string CorrelationId, Exception Exception)> Reports { get; } = [];
        public void Report(string correlationId, Exception exception) => Reports.Add((correlationId, exception));
    }
    private static T Get<T>(object model, string property) => (T)model.GetType().GetProperty(property)!.GetValue(model)!;
    private static void Set(object model, string property, object value) => model.GetType().GetProperty(property)!.SetValue(model, value);
    private static IReadOnlyList<object> Items(object model) => ((IEnumerable)Get<object>(model, "Items")).Cast<object>().ToArray();
    private static async Task<ViewPage> Page(Api api, Dialogs dialogs, Diagnostics? diagnostics = null)
    {
        var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), dialogs, diagnostics ?? new Diagnostics()));
        Assert.Empty(module.Views); await module.ActivateAsync();
        return Assert.Single(module.Views).Factory.Create();
    }
    [Fact]
    public Task Leaving_during_save_waits_and_duplicate_submission_is_disabled() => Sta.Run(async () =>
    {
        var pending = new TaskCompletionSource<OperationResult<TaskItemDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new Api { Creator = _ => pending.Task };
        var page = await Page(api, new()); var model = page.View.DataContext;
        Set(Get<object>(model, "Editor"), "Title", "draft");
        var command = Get<IAsyncRelayCommand>(model, "SaveCommand");
        var save = command.ExecuteAsync(null);
        Assert.False(command.CanExecute(null));
        Assert.False(Get<bool>(model, "CanEdit"));
        Assert.True(Get<bool>(model, "IsBusy"));
        Assert.Equal("正在保存任务…", Get<string>(model, "BusyFeedback"));
        var leave = page.Controller.CanLeaveAsync(); Assert.False(leave.IsCompleted);
        pending.SetResult(OperationResult<TaskItemDto>.Success(api.Item with { Title = "draft" }));
        await save; Assert.True(await leave); Assert.Equal(1, api.Creates);
        await page.Controller.DisposeAsync();
    });
    [Fact]
    public Task Save_failure_preserves_input_and_guard_blocks_exit_until_save_succeeds() => Sta.Run(async () =>
    {
        var api = new Api { SaveError = new(ErrorCodes.ValidationFailed, "Title is required", "Title") }; var dialogs = new Dialogs { Choice = UnsavedChoice.Save };
        var page = await Page(api, dialogs); await page.Controller.InitializeAsync();
        var model = page.View.DataContext; var editor = Get<object>(model, "Editor");
        Set(editor, "Title", "draft");
        Assert.False(await page.Controller.CanLeaveAsync());
        Assert.Equal("draft", Get<string>(editor, "Title")); Assert.Equal("Title is required", Get<string>(editor, "TitleError"));
        api.SaveError = null; Assert.True(await page.Controller.CanLeaveAsync());
        Assert.False(Get<bool>(editor, "IsDirty")); Assert.Equal(2, api.Creates);
        await page.Controller.DisposeAsync();
    });
    [Fact]
    public Task Conflict_preserves_editor_and_cancelled_delete_does_not_write() => Sta.Run(async () =>
    {
        var api = new Api(); var dialogs = new Dialogs(); var page = await Page(api, dialogs);
        var model = page.View.DataContext;
        await Get<IAsyncRelayCommand>(model, "SelectCommand").ExecuteAsync(api.Item);
        var editor = Get<object>(model, "Editor"); Set(editor, "Title", "my changes");
        api.SaveError = new(ErrorCodes.Conflict, "Reload required");
        await Get<IAsyncRelayCommand>(model, "SaveCommand").ExecuteAsync(null);
        Assert.Equal("my changes", Get<string>(editor, "Title")); Assert.True(Get<bool>(model, "HasConflict"));
        Assert.False(await page.Controller.CanLeaveAsync());
        await Get<IAsyncRelayCommand>(model, "DeleteCommand").ExecuteAsync(null); Assert.Equal(0, api.Deletes);
        await page.Controller.DisposeAsync();
    });
    [Fact]
    public Task Latest_filter_response_wins_and_dispatcher_remains_responsive() => Sta.Run(async () =>
    {
        var first = new TaskCompletionSource<OperationResult<IReadOnlyList<TaskItemDto>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<OperationResult<IReadOnlyList<TaskItemDto>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new Api { Query = q => q.Filter == TaskFilter.All ? first.Task : second.Task };
        var page = await Page(api, new()); var model = page.View.DataContext;
        var initial = page.Controller.InitializeAsync(); Assert.False(initial.IsCompleted);
        var heartbeat = false; await Dispatcher.CurrentDispatcher.InvokeAsync(() => heartbeat = true); Assert.True(heartbeat);
        Set(model, "FilterIndex", 1);
        second.SetResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([api.Item]));
        await Get<IAsyncRelayCommand>(model, "LoadCommand").ExecutionTask!;
        first.SetResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([])); await initial;
        Assert.Single(Items(model));
        Assert.True(page.View.Dispatcher.CheckAccess()); await page.Controller.DisposeAsync();
    });
    [Fact]
    public Task List_loading_program_errors_are_reported_with_a_correlation_id() => Sta.Run(async () =>
    {
        var diagnostics = new Diagnostics();
        var api = new Api { Query = _ => Task.FromException<OperationResult<IReadOnlyList<TaskItemDto>>>(new InvalidOperationException("broken")) };
        var page = await Page(api, new(), diagnostics);
        await page.Controller.InitializeAsync();
        var error = Get<string>(page.View.DataContext, "ListError");
        var report = Assert.Single(diagnostics.Reports);
        Assert.Contains(report.CorrelationId, error);
        Assert.IsType<InvalidOperationException>(report.Exception);
        Assert.Empty(Items(page.View.DataContext));
        Assert.False(Get<bool>(page.View.DataContext, "IsListEmpty"));
        Assert.Equal("", Get<string>(page.View.DataContext, "Error"));
        await page.Controller.DisposeAsync();
    });
    [Fact]
    public Task Failed_filter_load_clears_stale_rows_and_keeps_list_feedback_separate() => Sta.Run(async () =>
    {
        var api = new Api();
        api.Query = _ => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([api.Item]));
        var page = await Page(api, new()); await page.Controller.InitializeAsync();
        Assert.Single(Items(page.View.DataContext));

        api.Query = _ => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Failure(new(ErrorCodes.StorageUnavailable, "Storage is unavailable")));
        await Get<IAsyncRelayCommand>(page.View.DataContext, "LoadCommand").ExecuteAsync(null);

        Assert.Empty(Items(page.View.DataContext));
        Assert.Equal("Storage is unavailable", Get<string>(page.View.DataContext, "ListError"));
        Assert.True(Get<bool>(page.View.DataContext, "HasListError"));
        Assert.False(Get<bool>(page.View.DataContext, "IsListEmpty"));
        Assert.Equal("", Get<string>(page.View.DataContext, "Error"));
        await page.Controller.DisposeAsync();
    });
    [Fact]
    public Task Loading_retry_replaces_stale_error_and_cannot_run_concurrently() => Sta.Run(async () =>
    {
        var retry = new TaskCompletionSource<OperationResult<IReadOnlyList<TaskItemDto>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var api = new Api
        {
            Query = _ => ++calls == 1
                ? Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Failure(new(ErrorCodes.StorageUnavailable, "Storage is unavailable")))
                : retry.Task
        };
        var page = await Page(api, new());
        await page.Controller.InitializeAsync();
        var model = page.View.DataContext;

        Assert.True(Get<bool>(model, "HasListError"));
        Assert.True(Get<bool>(model, "CanRetry"));
        Assert.Equal(2, Panel.GetZIndex(Assert.IsType<Border>(page.View.FindName("TaskLoadingOverlay"))));

        var load = Get<IAsyncRelayCommand>(model, "LoadCommand").ExecuteAsync(null);
        Assert.True(Get<bool>(model, "IsLoading"));
        Assert.Equal("", Get<string>(model, "ListError"));
        Assert.False(Get<bool>(model, "HasListError"));
        Assert.False(Get<bool>(model, "CanRetry"));
        Assert.False(Get<bool>(model, "CanRefresh"));
        Assert.Equal(2, Panel.GetZIndex(Assert.IsType<Border>(page.View.FindName("TaskLoadingOverlay"))));
        Assert.Equal(1, Panel.GetZIndex(Assert.IsType<Border>(page.View.FindName("TaskErrorOverlay"))));

        retry.SetResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([]));
        await load;
        Assert.False(Get<bool>(model, "IsLoading"));
        Assert.True(Get<bool>(model, "CanRefresh"));
        Assert.True(Get<bool>(model, "IsListEmpty"));
        await page.Controller.DisposeAsync();
    });
    [Fact]
    public Task Field_errors_stay_in_the_editor_and_clear_with_the_matching_input() => Sta.Run(async () =>
    {
        var api = new Api { SaveError = new(ErrorCodes.ValidationFailed, "Title is required", "Title") };
        var page = await Page(api, new());
        var model = page.View.DataContext;
        var editor = Get<object>(model, "Editor");
        Set(editor, "Title", "draft");

        await Get<IAsyncRelayCommand>(model, "SaveCommand").ExecuteAsync(null);
        Assert.Equal("Title is required", Get<string>(editor, "TitleError"));
        Assert.True(Get<bool>(editor, "HasTitleError"));
        Assert.Equal("", Get<string>(model, "Error"));
        Assert.False(Get<bool>(model, "HasGeneralError"));

        Set(editor, "Title", "updated");
        Assert.Equal("", Get<string>(editor, "TitleError"));
        api.SaveError = new(ErrorCodes.StorageUnavailable, "Storage is unavailable");
        await Get<IAsyncRelayCommand>(model, "SaveCommand").ExecuteAsync(null);
        Assert.Equal("", Get<string>(editor, "TitleError"));
        Assert.Equal("Storage is unavailable", Get<string>(model, "Error"));
        Assert.True(Get<bool>(model, "HasGeneralError"));
        await page.Controller.DisposeAsync();
    });
    [Fact]
    public Task New_task_clears_the_previous_delete_feedback() => Sta.Run(async () =>
    {
        var api = new Api();
        var dialogs = new Dialogs { Delete = true };
        var page = await Page(api, dialogs);
        await page.Controller.InitializeAsync();
        var model = page.View.DataContext;

        await Get<IAsyncRelayCommand<TaskItemDto>>(model, "SelectCommand").ExecuteAsync(api.Item);
        await Get<IAsyncRelayCommand>(model, "DeleteCommand").ExecuteAsync(null);
        Assert.Equal("任务已删除", Get<string>(model, "Message"));
        Assert.Equal(1, api.Deletes);

        await Get<IAsyncRelayCommand>(model, "NewCommand").ExecuteAsync(null);
        Assert.Equal("", Get<string>(model, "Message"));
        Assert.Equal("", Get<string>(model, "Error"));
        Assert.Equal(0, Get<int>(model, "FilterIndex"));
        await page.Controller.DisposeAsync();
    });
    [Fact]
    public Task New_and_toggle_restore_the_all_filter_when_the_task_would_disappear() => Sta.Run(async () =>
    {
        var api = new Api();
        api.Query = _ => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([api.Item]));
        var page = await Page(api, new());
        await page.Controller.InitializeAsync();
        var model = page.View.DataContext;

        Set(model, "FilterIndex", 1);
        await Get<IAsyncRelayCommand>(model, "LoadCommand").ExecutionTask!;
        await Get<IAsyncRelayCommand>(model, "NewCommand").ExecuteAsync(null);
        Assert.Equal(0, Get<int>(model, "FilterIndex"));

        Set(model, "FilterIndex", 1);
        await Get<IAsyncRelayCommand>(model, "LoadCommand").ExecutionTask!;
        await Get<IAsyncRelayCommand<TaskItemDto>>(model, "SelectCommand").ExecuteAsync(api.Item);
        await Get<IAsyncRelayCommand>(model, "ToggleCommand").ExecuteAsync(null);
        Assert.Equal(0, Get<int>(model, "FilterIndex"));
        Assert.Equal("已标记完成", Get<string>(model, "Message"));
        await page.Controller.DisposeAsync();
    });
    [Fact]
    public Task Editor_keeps_the_action_bar_outside_the_form_scroller_and_lets_notes_grow() => Sta.Run(() =>
    {
        var editor = new Mosaic.Tasks.Wpf.Views.TaskEditorView();
        Arrange(editor, 520, 640);

        var form = Assert.IsType<ScrollViewer>(editor.FindName("FormScrollViewer"));
        var actionBar = Assert.IsType<Border>(editor.FindName("EditorActionBar"));
        var navigationBar = Assert.IsType<Border>(editor.FindName("CompactNavigationBar"));
        var notes = Assert.IsType<TextBox>(editor.FindName("NotesTextBox"));
        Assert.Equal(0, Grid.GetRow(navigationBar));
        Assert.Equal(1, Grid.GetRow(form));
        Assert.Equal(2, Grid.GetRow(actionBar));
        Assert.True(double.IsNaN(notes.Height));
        Assert.True(notes.MinHeight >= 120);
        Assert.True(notes.MaxHeight >= notes.MinHeight);
        return Task.CompletedTask;
    });
    [Fact]
    public Task Muted_text_meets_readability_targets_and_all_system_palettes_load() => Sta.Run(() =>
    {
        var light = LoadTheme();
        var muted = Assert.IsType<SolidColorBrush>(light[MosaicTheme.TextMutedBrushKey]).Color;
        var surface = Assert.IsType<SolidColorBrush>(light[MosaicTheme.SurfaceBrushKey]).Color;
        var selected = Assert.IsType<SolidColorBrush>(light[MosaicTheme.AccentSoftBrushKey]).Color;
        Assert.True(ContrastRatio(muted, surface) >= 4.5);
        Assert.True(ContrastRatio(muted, selected) >= 4.5);

        foreach (var name in new[] { "Light", "Dark", "HighContrast" })
        {
            var uri = new Uri($"/Mosaic.Presentation.Wpf;component/Themes/Palettes/{name}.xaml", UriKind.Relative);
            var palette = Assert.IsType<ResourceDictionary>(Application.LoadComponent(uri));
            var keys = palette.Keys.Cast<object>().ToArray();
            Assert.Contains(MosaicTheme.CanvasBrushKey, keys);
            Assert.Contains(MosaicTheme.SurfaceBrushKey, keys);
            Assert.Contains(MosaicTheme.TextPrimaryBrushKey, keys);
            Assert.Contains(MosaicTheme.AccentButtonBackgroundBrushKey, keys);
        }
        var root = new ResourceDictionary();
        root.MergedDictionaries.Add(LoadTheme());
        MosaicThemeManager.Apply(root, MosaicThemeVariant.Dark);
        Assert.Equal(Color.FromRgb(0x0F, 0x15, 0x12), Assert.IsType<SolidColorBrush>(root[MosaicTheme.CanvasBrushKey]).Color);
        Assert.Equal(Color.FromRgb(0xE6, 0xF0, 0xE9), Assert.IsType<SolidColorBrush>(root[MosaicTheme.TextPrimaryBrushKey]).Color);
        MosaicThemeManager.Apply(root, MosaicThemeVariant.Light);
        Assert.Equal(Color.FromRgb(0xF7, 0xF9, 0xF5), Assert.IsType<SolidColorBrush>(root[MosaicTheme.CanvasBrushKey]).Color);
        return Task.CompletedTask;
    });
    [Fact]
    public Task Navigation_uses_the_icon_contract_and_tracks_the_active_item() => Sta.Run(async () =>
    {
        var registry = new ContributionRegistry();
        var module = new TasksUiModule(new(new Api(), new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        var navigation = Assert.Single(module.Navigation);
        Assert.Equal("任务", navigation.Label);
        Assert.Equal("check-square", navigation.IconKey);

        var registration = registry.Register(module.Navigation, module.Views);
        var shell = new ShellWindow(registry, new Dialogs());
        await shell.InitializeAsync();
        shell.ShowInTaskbar = false;
        shell.WindowStyle = WindowStyle.None;
        shell.Opacity = 0;
        shell.Show();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal("任务", Get<string>(shell.DataContext, "PageTitle"));
        Assert.Equal(navigation.Id, Get<string?>(shell.DataContext, "CurrentNavigationId"));
        Assert.Single(Assert.IsType<ItemsControl>(shell.FindName("NavigationItems")).Items);
        var navigationText = FindVisualChildren<TextBlock>(shell)
            .Select(text => text.Text)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("✓", navigationText);
        Assert.Contains("任务", navigationText);

        await shell.DisposeAsync();
        registration.Dispose();
        shell.Close();
    });
    [Fact]
    public Task Empty_state_copy_and_count_follow_the_active_filter() => Sta.Run(async () =>
    {
        var page = await Page(new Api(), new()); await page.Controller.InitializeAsync();
        var model = page.View.DataContext; var filter = model.GetType().GetProperty("FilterIndex")!;

        filter.SetValue(model, 1); await Get<IAsyncRelayCommand>(model, "LoadCommand").ExecutionTask!;
        Assert.Equal("没有未完成任务", Get<string>(model, "EmptyTitle"));
        Assert.Equal("0 项未完成", Get<string>(model, "CountLabel"));

        filter.SetValue(model, 2); await Get<IAsyncRelayCommand>(model, "LoadCommand").ExecutionTask!;
        Assert.Equal("还没有已完成的任务", Get<string>(model, "EmptyTitle"));
        Assert.Equal("0 项已完成", Get<string>(model, "CountLabel"));
        await page.Controller.DisposeAsync();
    });
    [Fact]
    public Task Selecting_a_list_row_loads_the_editor_and_keeps_the_row_selected() => Sta.Run(async () =>
    {
        var api = new Api();
        api.Query = _ => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([api.Item]));
        var page = await Page(api, new()); await page.Controller.InitializeAsync();
        page.View.Resources.MergedDictionaries.Add(LoadTheme());
        var model = page.View.DataContext; var row = Assert.Single(Items(model));
        Arrange(page.View, 900, 650);
        var list = Assert.IsType<ListBox>(page.View.FindName("TaskListBox"));
        Assert.Single(list.Items);

        list.SelectedIndex = 0;
        page.View.UpdateLayout();

        Assert.Same(row, Get<object>(model, "SelectedItem"));
        var current = Assert.IsType<TaskItemDto>(Get<object>(Get<object>(model, "Editor"), "Current"));
        Assert.Equal(api.Item.Id, current.Id);
        list.UpdateLayout();
        var container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        container.ApplyTemplate();
        var rowBorder = Assert.IsType<Border>(container.Template.FindName("TaskRow", container));
        Assert.Equal(Color.FromRgb(0xDD, 0xEA, 0xE1), Assert.IsType<SolidColorBrush>(rowBorder.Background).Color);
        await page.Controller.DisposeAsync();
    });
    [Fact]
    public Task Accent_button_text_and_disabled_colors_are_not_overridden_by_a_textblock_style() => Sta.Run(() =>
    {
        var resources = LoadTheme();
        Assert.DoesNotContain(typeof(TextBlock), resources.Keys.Cast<object>());

        var label = new TextBlock { Text = "保存任务" };
        var button = new Button { Content = label, Style = Assert.IsType<Style>(resources[MosaicTheme.AccentButtonKey]) };
        var host = new Grid { Resources = resources };
        host.Children.Add(button);
        var size = new Size(240, 80);
        host.Measure(size); host.Arrange(new Rect(size)); host.UpdateLayout();

        Assert.Equal(Color.FromRgb(255, 255, 255), Assert.IsType<SolidColorBrush>(label.Foreground).Color);
        button.IsEnabled = false; host.UpdateLayout();
        Assert.Equal(Color.FromRgb(0x71, 0x80, 0x77), Assert.IsType<SolidColorBrush>(label.Foreground).Color);
        return Task.CompletedTask;
    });
    [Fact]
    public Task Shared_theme_provides_custom_combo_box_popup_and_item_templates() => Sta.Run(() =>
    {
        var resources = LoadTheme();
        var comboBox = new ComboBox
        {
            Style = Assert.IsType<Style>(resources[typeof(ComboBox)]),
            ItemsSource = new[] { "全部任务", "未完成", "已完成" },
            SelectedIndex = 0
        };
        var option = new ComboBoxItem
        {
            Content = "未完成",
            Style = Assert.IsType<Style>(resources[typeof(ComboBoxItem)])
        };
        var host = new Grid { Resources = resources };
        host.Children.Add(comboBox);
        host.Children.Add(option);

        Arrange(host, 320, 120);
        comboBox.ApplyTemplate();
        option.ApplyTemplate();

        var popup = Assert.IsType<Popup>(comboBox.Template.FindName("PART_Popup", comboBox));
        Assert.Equal(PlacementMode.Bottom, popup.Placement);
        var popupSurface = Assert.IsType<Border>(popup.Child);
        Assert.Equal(new CornerRadius(9), popupSurface.CornerRadius);
        Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), Assert.IsType<SolidColorBrush>(popupSurface.Background).Color);

        var optionSurface = Assert.IsType<Border>(option.Template.FindName("ItemBorder", option));
        Assert.Equal(new CornerRadius(7), optionSurface.CornerRadius);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(optionSurface.Background).Color);
        return Task.CompletedTask;
    });
    [Fact]
    public void Tasks_wpf_owns_its_ui_and_does_not_depend_on_shell_private_resources()
    {
        var root = FindRepositoryRoot();
        var projectRoot = Path.Combine(root, "src", "Modules", "Tasks", "Mosaic.Tasks.Wpf");
        var files = Directory.GetFiles(projectRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && Path.GetExtension(path) is ".cs" or ".xaml" or ".csproj");

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("Mosaic.Shell.Wpf", source);
            Assert.DoesNotContain("{DynamicResource AccentButton}", source);
            Assert.DoesNotContain("{DynamicResource TextSecondaryBrush}", source);
            Assert.DoesNotContain("{DynamicResource DangerBrush}", source);
            Assert.DoesNotContain("{DynamicResource FocusBrush}", source);
        }
    }
    [Fact]
    public Task Empty_contributions_create_empty_shell_and_duplicate_contributions_are_atomic() => Sta.Run(async () =>
    {
        var registry = new ContributionRegistry();
        var shell = new ShellWindow(registry, new Dialogs()); await shell.InitializeAsync();
        Assert.Null(Get<FrameworkElement?>(shell.DataContext, "Content"));
        Assert.Empty(Get<IReadOnlyList<NavigationItem>>(shell.DataContext, "Navigation"));
        Assert.Equal(Visibility.Visible, ((UIElement)shell.FindName("EmptyWorkspace")!).Visibility);
        var module = new TasksUiModule(new(new Api(), new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        var registration = registry.Register(module.Navigation, module.Views);
        Assert.Throws<InvalidOperationException>(() => { registry.Register(module.Navigation, module.Views); });
        Assert.Single(registry.Navigation);
        Assert.Throws<InvalidOperationException>(() => { new ContributionRegistry().Register(module.Navigation, []); });
        await Get<IAsyncRelayCommand>(shell.DataContext, "NavigateCommand").ExecuteAsync(registry.Navigation[0]);
        Assert.NotNull(Get<FrameworkElement?>(shell.DataContext, "Content"));
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(Visibility.Collapsed, ((UIElement)shell.FindName("EmptyWorkspace")!).Visibility);
        registration.Dispose();
        registration.Dispose();
        Assert.Empty(registry.Navigation);
        Assert.Null(Get<FrameworkElement?>(shell.DataContext, "Content"));
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(Visibility.Visible, ((UIElement)shell.FindName("EmptyWorkspace")!).Visibility);
        await shell.DisposeAsync();
        shell.Close();
    });
    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mosaic.sln"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate Mosaic.sln.");
    }
    private static ResourceDictionary LoadTheme()
    {
        var uri = new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative);
        return Assert.IsType<ResourceDictionary>(Application.LoadComponent(uri));
    }
    private static void Arrange(FrameworkElement element, double width, double height)
    {
        var size = new Size(width, height);
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
    }
    private static double ContrastRatio(Color first, Color second)
    {
        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }
    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
