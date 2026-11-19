using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Settings.Core;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Wpf;
using Mosaic.Shell.Wpf;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Wpf;
using Mosaic.Tasks.Wpf.Internal;
using Mosaic.Tasks.Wpf.Views;

namespace Mosaic.Wpf.Tests;

public sealed class PresentationTests
{
    private sealed class Dialogs : IUserDialogService
    {
        internal UnsavedChoice Choice = UnsavedChoice.Cancel;
        internal bool Delete = false;
        internal int UnsavedPrompts;
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Delete);
        public Task<bool> ConfirmDeleteAsync(string title) => Task.FromResult(Delete);
        public Task<UnsavedChoice> AskUnsavedChangesAsync() { UnsavedPrompts++; return Task.FromResult(Choice); }
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
        internal Func<SetTaskArchivedRequest, Task<OperationResult<TaskItemDto>>>? Archive;
        public Task<OperationResult<TaskItemDto>> SetArchivedAsync(SetTaskArchivedRequest request, CancellationToken cancellationToken = default) => Archive?.Invoke(request) ?? throw new NotSupportedException();
        public Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) { Deletes++; return Task.FromResult(OperationResult.Success()); }
        public Task<OperationResult<TaskConstraintsDto>> GetConstraintsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<TaskConstraintsDto>.Success(new(200, 20_000, true, false, 1)));
    }
    private sealed class Diagnostics : IDiagnosticSink
    {
        internal List<(string CorrelationId, Exception Exception)> Reports { get; } = [];
        public void Report(string correlationId, Exception exception) => Reports.Add((correlationId, exception));
    }
    private sealed class SettingsCore : ISettingsCore
    {
        internal Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
        public List<ISettingSource> Sources { get; } = [];
        public Task<OperationResult<IReadOnlyList<SettingDefinition>>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<SettingDefinition>>.Success([]));
        public Task<OperationResult<IReadOnlyDictionary<string, object?>>> GetValuesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, object?>>.Success(Values));
        public Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
            IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, OperationError>>.Success(new Dictionary<string, OperationError>()));
        public Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());
        public Task<OperationResult> ResetAsync(IReadOnlyList<string> definitionIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());
        public IDisposable Register(ISettingSource source)
        {
            Sources.Add(source);
            return new Registration(() => Sources.Remove(source));
        }
        private sealed class Registration(Action onDispose) : IDisposable
        {
            private int _disposed;
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose(); }
        }
    }
    private sealed class SettingsApi : ISettingsApi
    {
        public Task<OperationResult<ISettingsEditSession>> BeginEditSessionAsync(CancellationToken cancellationToken = default) => TestSettingsSession.OpenAsync(this);
        public List<IReadOnlyDictionary<string, object?>> SaveCalls { get; } = [];
        public IReadOnlyList<SettingDefinition> Definitions { get; init; } = [];
        public IReadOnlyDictionary<string, object?>? Values { get; init; }
        public IReadOnlyDictionary<string, OperationError>? ProposedErrors { get; init; }
        public Task<OperationResult<IReadOnlyList<SettingDefinition>>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<SettingDefinition>>.Success(Definitions));
        public Task<OperationResult<IReadOnlyDictionary<string, object?>>> GetValuesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, object?>>.Success(
                Values ?? Definitions.ToDictionary(item => item.Id, item => item.DefaultValue, StringComparer.Ordinal)));
        public Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
            IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default) =>
            RecordChanges(changes);
        private Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> RecordChanges(IReadOnlyDictionary<string, object?> changes)
        {
            SaveCalls.Add(changes);
            return Task.FromResult(OperationResult<IReadOnlyDictionary<string, OperationError>>.Success(
                ProposedErrors ?? new Dictionary<string, OperationError>()));
        }
        public Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());
        public List<IReadOnlyList<string>> ResetCalls { get; } = [];
        public Task<OperationResult> ResetAsync(IReadOnlyList<string> definitionIds, CancellationToken cancellationToken = default)
        {
            ResetCalls.Add(definitionIds);
            return Task.FromResult(OperationResult.Success());
        }
    }
    private static object CreateSettingsViewModel(ISettingsApi api, Dialogs dialogs)
    {
        var type = typeof(SettingsUiModule).Assembly.GetType("Mosaic.Settings.Wpf.Internal.SettingsViewModel")!;
        return Activator.CreateInstance(type, api, dialogs)!;
    }
    private static Task Initialize(object model) => (Task)model.GetType().GetMethod("InitializeAsync")!.Invoke(model, null)!;
    private static void SetProperty(object model, string property, object value) => model.GetType().GetProperty(property)!.SetValue(model, value);
    private static IList VisibleRows(object model) => (IList)model.GetType().GetProperty("VisibleRows")!.GetValue(model)!;
    private static IList<string> RowIds(object model) => VisibleRows(model).Cast<object>().Select(row => (string)row.GetType().GetProperty("Id")!.GetValue(row)!).ToList();
    private static T Get<T>(object model, string property) => (T)model.GetType().GetProperty(property)!.GetValue(model)!;
    private static void Set(object model, string property, object value) => model.GetType().GetProperty(property)!.SetValue(model, value);
    private static IReadOnlyList<object> Items(object model) => ((IEnumerable)Get<object>(model, "Items")).Cast<object>().ToArray();
    private static async Task<ViewPage> Page(Api api, Dialogs dialogs, Diagnostics? diagnostics = null)
    {
        var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), dialogs, diagnostics ?? new Diagnostics()));
        Assert.Empty(module.Views); await module.ActivateAsync();
        return Assert.Single(module.Views, view => view.Id == "tasks.list").Factory.Create();
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
    public Task Editor_keeps_the_action_bar_outside_the_form_scroller_and_lets_notes_grow() => Sta.Run(async () =>
    {
        var page = await Page(new Api(), new Dialogs());
        var editor = Assert.IsType<Mosaic.Tasks.Wpf.Views.TaskEditorView>(page.View.FindName("TaskEditorPane"));
        Arrange(editor, 520, 640);

        var form = Assert.IsType<ScrollViewer>(editor.FindName("FormScrollViewer"));
        var actionBar = Assert.IsType<Border>(editor.FindName("EditorActionBar"));
        var notes = Assert.IsType<TextBox>(editor.FindName("NotesTextBox"));
        Assert.Equal(1, Grid.GetRow(form));
        Assert.Equal(2, Grid.GetRow(actionBar));
        Assert.True(double.IsNaN(notes.Height));
        Assert.True(notes.MinHeight >= 120);
        Assert.True(double.IsPositiveInfinity(notes.MaxHeight));
        await page.Controller.DisposeAsync();
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
        Assert.Equal(2, module.Navigation.Count);
        var navigation = module.Navigation;
        Assert.Equal("任务", navigation[0].Label);
        Assert.Equal("check-square", navigation[0].IconKey);
        Assert.Equal("tasks.navigation", navigation[0].Id);
        Assert.Equal("tasks.list", navigation[0].ViewId);
        Assert.Equal(100, navigation[0].Order);
        Assert.Equal("Tasks.Navigation", navigation[0].LabelKey);
        Assert.Equal("已归档任务", navigation[1].Label);
        Assert.Equal("folder", navigation[1].IconKey);
        Assert.Equal("tasks.archived.navigation", navigation[1].Id);
        Assert.Equal("tasks.archived.list", navigation[1].ViewId);
        Assert.Equal(110, navigation[1].Order);
        Assert.Equal("Tasks.Archived.Navigation", navigation[1].LabelKey);
        Assert.NotEqual(navigation[0].ViewId, navigation[1].ViewId);

        var registration = registry.Register(module.Navigation, module.Views);
        var shell = new ShellWindow(registry, new Dialogs());
        shell.Resources.MergedDictionaries.Add(LoadTheme());
        await shell.InitializeAsync();
        shell.ShowInTaskbar = false;
        shell.WindowStyle = WindowStyle.None;
        shell.Opacity = 0;
        shell.Show();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal("任务", Get<string>(shell.DataContext, "PageTitle"));
        Assert.Equal(navigation[0].Id, Get<string?>(shell.DataContext, "CurrentNavigationId"));
        Assert.Equal(2, Assert.IsType<ItemsControl>(shell.FindName("NavigationItems")).Items.Count);
        var navigationText = FindVisualChildren<TextBlock>(shell)
            .Select(text => text.Text)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("任务", navigationText);
        var glyphs = FindVisualChildren<TextBlock>(shell)
            .Where(text => text.Name == "NavigationGlyph")
            .ToArray();
        Assert.All(glyphs, glyph => Assert.Equal("Segoe MDL2 Assets", glyph.FontFamily.Source));
        Assert.All(glyphs, glyph => Assert.Equal(16, glyph.FontSize));
        Assert.Contains(glyphs, glyph => glyph.Text == "\uE9D5");
        Assert.Contains(glyphs, glyph => glyph.Text == "\uE8B7");

        Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(shell.FindName("StatusBar")).Visibility);

        await shell.DisposeAsync();
        registration.Dispose();
        shell.Close();
    });
    [Fact]
    public Task Status_bar_shows_module_status_and_opens_its_view() => Sta.Run(async () =>
    {
        var registry = new ContributionRegistry();
        var module = new TasksUiModule(new(new Api(), new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        var status = new StatusStub { Text = "小明", Detail = "demo@example.com", ToolTip = "小明，点击进入账户页" };
        using var registration = registry.Register(module.Navigation, module.Views, [status]);
        var shell = new ShellWindow(registry, new Dialogs());
        shell.Resources.MergedDictionaries.Add(LoadTheme());
        await shell.InitializeAsync();
        shell.ShowInTaskbar = false;
        shell.WindowStyle = WindowStyle.None;
        shell.Opacity = 0;
        shell.Show();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var bar = Assert.IsType<Border>(shell.FindName("StatusBar"));
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Single(Assert.IsType<ItemsControl>(shell.FindName("StatusItemsControl")).Items);
        var texts = FindVisualChildren<TextBlock>(shell).Select(text => text.Text).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("小明", texts);
        Assert.Contains("demo@example.com", texts);

        var navigate = Assert.IsAssignableFrom<IAsyncRelayCommand<string?>>(Get<object>(shell.DataContext, "NavigateToViewCommand"));
        await navigate.ExecuteAsync(status.TargetViewId);
        Assert.Equal("tasks.archived.navigation", Get<string?>(shell.DataContext, "CurrentNavigationId"));

        status.Text = "小红";
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Contains("小红", FindVisualChildren<TextBlock>(shell).Select(text => text.Text));

        await shell.DisposeAsync();
        shell.Close();
    });

    private sealed class StatusStub : IStatusContribution
    {
        private string _text = "";
        private string? _detail;
        private string? _toolTip;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public string Id => "test.status";
        public string Glyph => "\uE77B";
        public string? TargetViewId => "tasks.archived.list";
        public int Order => 0;
        public string Text { get => _text; set { _text = value; Raise(); } }
        public string? Detail { get => _detail; set { _detail = value; Raise(); } }
        public string? ToolTip { get => _toolTip; set { _toolTip = value; Raise(); } }
        private void Raise() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
    }
    [Fact]
    public Task Shell_navigation_labels_and_page_title_follow_the_ui_language() => Sta.Run(async () =>
    {
        MosaicLocalizationManager.SetLanguage(MosaicLanguage.Chinese);
        var registry = new ContributionRegistry();
        var module = new TasksUiModule(new(new Api(), new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        using var registration = registry.Register(module.Navigation, module.Views);
        var shell = new ShellWindow(registry, new Dialogs());
        try
        {
            await shell.InitializeAsync();
            Assert.Equal("任务", Get<string>(shell.DataContext, "PageTitle"));
            var navigation = (IReadOnlyList<NavigationItem>)Get<object>(shell.DataContext, "Navigation");
            Assert.Equal(2, navigation.Count);
            Assert.Equal("任务", navigation[0].Label);
            Assert.Equal("已归档任务", navigation[1].Label);

            MosaicLocalizationManager.SetLanguage(MosaicLanguage.English);

            Assert.Equal("Tasks", Get<string>(shell.DataContext, "PageTitle"));
            Assert.Equal("Tasks", navigation[0].Label);
            Assert.Equal("Archived tasks", navigation[1].Label);
        }
        finally
        {
            MosaicLocalizationManager.SetLanguage(MosaicLanguage.Chinese);
            await shell.DisposeAsync();
        }
    });
    [Fact]
    public Task Shell_shows_the_product_version_and_commit() => Sta.Run(async () =>
    {
        var shell = new ShellWindow(new ContributionRegistry(), new Dialogs());
        try
        {
            await shell.InitializeAsync();
            Assert.Matches(@"^Mosaic v\d+\.\d+\.\d+", shell.Title);
            var version = Assert.IsType<TextBlock>(shell.FindName("VersionText"));
            Assert.Matches(@"^v\d+\.\d+\.\d+", version.Text);
        }
        finally
        {
            await shell.DisposeAsync();
        }
    });

    [Fact]

    public Task Task_editor_and_count_text_follow_the_ui_language() => Sta.Run(async () =>
    {
        MosaicLocalizationManager.SetLanguage(MosaicLanguage.Chinese);
        var api = new Api();
        api.Query = _ => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([api.Item]));
        var page = await Page(api, new());
        try
        {
            await page.Controller.InitializeAsync();
            var select = Assert.IsAssignableFrom<IAsyncRelayCommand<TaskItemDto>>(
                page.View.DataContext.GetType().GetProperty("SelectCommand")!.GetValue(page.View.DataContext));
            await select.ExecuteAsync(api.Item);
            var editor = Get<object>(page.View.DataContext, "Editor");
            Assert.Equal("任务详情", Get<string>(editor, "Heading"));
            Assert.Equal("1 项任务", Get<string>(page.View.DataContext, "CountLabel"));

            MosaicLocalizationManager.SetLanguage(MosaicLanguage.English);

            Assert.Equal("Task details", Get<string>(editor, "Heading"));
            Assert.Equal("1 task", Get<string>(page.View.DataContext, "CountLabel"));
        }
        finally
        {
            MosaicLocalizationManager.SetLanguage(MosaicLanguage.Chinese);
            await page.Controller.DisposeAsync();
        }
    });
    [Fact]
    public Task Archived_navigation_opens_an_independent_archived_page() => Sta.Run(async () =>
    {
        MosaicLocalizationManager.SetLanguage(MosaicLanguage.Chinese);
        var archivedTask = new TaskItemDto(Guid.NewGuid(), "已归档", "旧笔记", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7) { ArchivedAtUtc = DateTimeOffset.UtcNow.AddDays(-2) };
        var api = new Api();
        api.Query = query => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success(
            query.ArchiveScope == TaskArchiveScope.Archived ? [archivedTask] : (IReadOnlyList<TaskItemDto>)[]));
        var registry = new ContributionRegistry();
        var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        using var registration = registry.Register(module.Navigation, module.Views);
        var shell = new ShellWindow(registry, new Dialogs());
        shell.Resources.MergedDictionaries.Add(LoadTheme());
        try
        {
            await shell.InitializeAsync();
            var archivedNavigation = Assert.Single(registry.Navigation, item => item.Id == "tasks.archived.navigation");
            await Get<IAsyncRelayCommand>(shell.DataContext, "NavigateCommand").ExecuteAsync(archivedNavigation);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal("已归档任务", Get<string>(shell.DataContext, "PageTitle"));
            Assert.Equal("tasks.archived.navigation", Get<string?>(shell.DataContext, "CurrentNavigationId"));
            var content = Assert.IsAssignableFrom<FrameworkElement>(Get<object>(shell.DataContext, "Content"));
            var model = content.DataContext;
            Assert.Equal(3, Get<int>(model, "FilterIndex"));
            Assert.Equal("已归档任务", Get<string>(model, "ListTitle"));
            Assert.Equal("1 项已归档", Get<string>(model, "CountLabel"));

            var page = Assert.IsType<TasksView>(content);
            Arrange(page, 900, 650);
            var title = FindVisualChildren<TextBlock>(page)
                .Single(text => text.Text is "已归档任务" or "任务清单");
            Assert.Equal("已归档任务", title.Text);
            var newTask = Assert.Single(FindVisualChildren<Button>(page),
                button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "NewTask");
            Assert.Equal(Visibility.Collapsed, newTask.Visibility);
            var filter = Assert.Single(FindVisualChildren<ComboBox>(page),
                combo => System.Windows.Automation.AutomationProperties.GetAutomationId(combo) == "TaskFilter");
            Assert.Equal(Visibility.Collapsed, filter.Visibility);
        }
        finally
        {
            MosaicLocalizationManager.SetLanguage(MosaicLanguage.Chinese);
            await shell.DisposeAsync();
            shell.Close();
        }
    });
    [Fact]
    public Task Archived_page_restores_a_task_and_keeps_the_page_on_the_archived_list() => Sta.Run(async () =>
    {
        MosaicLocalizationManager.SetLanguage(MosaicLanguage.Chinese);
        var archivedTask = new TaskItemDto(Guid.NewGuid(), "已归档", "", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7) { ArchivedAtUtc = DateTimeOffset.UtcNow.AddDays(-2) };
        var api = new Api { Item = archivedTask };
        var restored = 0;
        var restoredRequests = new List<SetTaskArchivedRequest>();
        api.Query = query => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success(
            query.ArchiveScope == TaskArchiveScope.Archived && restored == 0 ? [archivedTask] : (IReadOnlyList<TaskItemDto>)[]));
        api.Archive = request =>
        {
            restoredRequests.Add(request);
            if (!request.IsArchived) restored++;
            return Task.FromResult(OperationResult<TaskItemDto>.Success(archivedTask with
            {
                ArchivedAtUtc = request.IsArchived ? archivedTask.ArchivedAtUtc ?? DateTimeOffset.UtcNow : null,
                RestoredAtUtc = request.IsArchived ? archivedTask.RestoredAtUtc : DateTimeOffset.UtcNow,
                Revision = archivedTask.Revision + 1
            }));
        };
        var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        var contribution = Assert.Single(module.Views, view => view.Id == "tasks.archived.list");
        var page = contribution.Factory.Create();
        try
        {
            await page.Controller.InitializeAsync();
            var model = page.View.DataContext;
            Assert.Equal(3, Get<int>(model, "FilterIndex"));
            Assert.Equal("已归档任务", Get<string>(model, "ListTitle"));
            Assert.Equal("1 项已归档", Get<string>(model, "CountLabel"));

            var select = Assert.IsAssignableFrom<IAsyncRelayCommand<TaskItemDto>>(model.GetType().GetProperty("SelectCommand")!.GetValue(model));
            await select.ExecuteAsync(archivedTask);
            Assert.True(Get<bool>(model, "IsEditorOpen"));

            var archive = Assert.IsAssignableFrom<IAsyncRelayCommand>(model.GetType().GetProperty("ArchiveCommand")!.GetValue(model));
            await archive.ExecuteAsync(null);
            var request = Assert.Single(restoredRequests);
            Assert.Equal(archivedTask.Id, request.Id);
            Assert.False(request.IsArchived);
            Assert.Equal(archivedTask.Revision, request.ExpectedRevision);
            Assert.Equal("任务已恢复到活跃列表。", Get<string>(model, "Message"));
            Assert.True(Get<bool>(model, "HasMessage"));
            Assert.False(Get<bool>(model, "IsEditorOpen"));
            Assert.Equal("没有归档任务", Get<string>(model, "EmptyTitle"));
            Assert.True(Get<bool>(model, "IsListEmpty"));
            Assert.Equal(3, Get<int>(model, "FilterIndex"));
        }
        finally
        {
            MosaicLocalizationManager.SetLanguage(MosaicLanguage.Chinese);
            await page.Controller.DisposeAsync();
        }
    });
    [Fact]
    public Task Archived_page_keeps_its_archived_filter_and_active_page_follows_the_default_filter() => Sta.Run(async () =>
    {
        var api = new Api();
        var settings = new SettingsCore();
        settings.Values["tasks.defaultFilter"] = "Open";
        var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics(), settings));
        await module.ActivateAsync();
        var archivedContribution = Assert.Single(module.Views, view => view.Id == "tasks.archived.list");
        var archivedPage = archivedContribution.Factory.Create();
        var activeContribution = Assert.Single(module.Views, view => view.Id == "tasks.list");
        var activePage = activeContribution.Factory.Create();
        try
        {
            await archivedPage.Controller.InitializeAsync();
            await activePage.Controller.InitializeAsync();
            Assert.Equal(3, Get<int>(archivedPage.View.DataContext, "FilterIndex"));
            Assert.Equal(1, Get<int>(activePage.View.DataContext, "FilterIndex"));
        }
        finally
        {
            await activePage.Controller.DisposeAsync();
            await archivedPage.Controller.DisposeAsync();
        }
    });
    [Fact]
    public Task Archived_page_locks_its_filter_when_settings_arrive_late() => Sta.Run(async () =>
    {
        var api = new Api();
        var target = new TaskUiSettingsTarget();
        var model = new TasksViewModel(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics(),
            null, target, null, archivedOnly: true);
        try
        {
            Assert.Equal(3, Get<int>(model, "FilterIndex"));
            Assert.False(Get<bool>(model, "ShowNewTask"));
            Assert.False(Get<bool>(model, "ShowFilterChooser"));
            Assert.False(Get<IAsyncRelayCommand>(model, "NewCommand").CanExecute(null));

            Set(model, "FilterIndex", 1);
            Assert.Equal(3, Get<int>(model, "FilterIndex"));
            Set(model, "FilterIndex", 0);
            Assert.Equal(3, Get<int>(model, "FilterIndex"));

            target.Apply(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tasks.defaultFilter"] = "Open",
                ["tasks.confirmDelete"] = true
            });
            Assert.Equal(3, Get<int>(model, "FilterIndex"));
            Assert.Equal("已归档任务", Get<string>(model, "ListTitle"));
            Assert.False(Get<bool>(model, "ShowNewTask"));
            Assert.False(Get<bool>(model, "ShowFilterChooser"));

            await model.InitializeAsync();
            Assert.Equal(3, Get<int>(model, "FilterIndex"));
            Assert.False(Get<IAsyncRelayCommand>(model, "NewCommand").CanExecute(null));
        }
        finally
        {
            await model.DisposeAsync();
        }
    });
    [Fact]
    public Task Settings_row_text_follows_the_ui_language() => Sta.Run(async () =>
    {
        MosaicLocalizationManager.SetLanguage(MosaicLanguage.Chinese);
        var api = new SettingsApi
        {
            Definitions = new ShellSettingSource().Definitions.ToArray()
        };
        var module = new SettingsUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        var page = Assert.Single(module.Views).Factory.Create();
        try
        {
            await page.Controller.InitializeAsync();
            SetProperty(page.View.DataContext, "ShowAdvanced", true);
            SetProperty(page.View.DataContext, "SelectedSection", Enum.Parse(Get<object>(page.View.DataContext, "SelectedSection").GetType(), "Appearance"));
            static string RowText(object model, string id, string property)
            {
                var row = VisibleRows(model).Cast<object>()
                    .Single(item => (string)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
                return (string)row.GetType().GetProperty(property)!.GetValue(row)!;
            }

            Assert.Equal("主题", RowText(page.View.DataContext, "appearance.theme", "DisplayName"));
            Assert.Equal("界面语言", RowText(page.View.DataContext, "ui.language", "DisplayName"));

            MosaicLocalizationManager.SetLanguage(MosaicLanguage.English);

            Assert.Equal("Theme", RowText(page.View.DataContext, "appearance.theme", "DisplayName"));
            Assert.Equal("Interface language", RowText(page.View.DataContext, "ui.language", "DisplayName"));
        }
        finally
        {
            MosaicLocalizationManager.SetLanguage(MosaicLanguage.Chinese);
            await page.Controller.DisposeAsync();
        }
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
        Assert.Equal(2, registry.Navigation.Count);
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

    [Fact]
    public Task Integer_row_typing_marks_dirty_and_invalid_text_reports_error() => Sta.Run(() =>
    {
        var rowType = typeof(SettingsUiModule).Assembly.GetType("Mosaic.Settings.Wpf.Internal.SettingRow")!;
        var definition = new SettingDefinition(
            "tasks.title.maxCharacters", "tasks-core", "Title", "Description",
            SettingValueKind.Integer, 200, 1, 1000, null,
            SettingsGroup.Tasks, SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.UserSettings);
        var row = Activator.CreateInstance(rowType, definition, 200)!;
        var textValue = rowType.GetProperty("TextValue")!;
        var isDirty = rowType.GetProperty("IsDirty")!;
        var error = rowType.GetProperty("Error")!;

        textValue.SetValue(row, "50");
        Assert.True((bool)isDirty.GetValue(row)!);
        Assert.Null(error.GetValue(row));

        textValue.SetValue(row, "12a");
        Assert.Equal("请输入整数。", error.GetValue(row));
        Assert.True((bool)isDirty.GetValue(row)!);

        textValue.SetValue(row, "200");
        Assert.Null(error.GetValue(row));
        Assert.False((bool)isDirty.GetValue(row)!);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Tasks_ui_module_registers_definitions_including_text_overrides() => Sta.Run(async () =>
    {
        var settings = new SettingsCore();
        var module = new TasksUiModule(new(new Api(), new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics(), settings));
        await module.ActivateAsync();
        var source = Assert.Single(settings.Sources);
        Assert.Equal("tasks-ui", source.ModuleId);
        Assert.Equal(26, source.Definitions.Count);
        var floating = Assert.Single(source.Definitions, definition => definition.Id == "tasks.floating.allowNotesEditing");
        Assert.Equal(SettingsGroup.FloatingWindows, floating.Group);
        Assert.Equal(false, floating.DefaultValue);
        Assert.Equal(SettingPreviewMode.Reversible, floating.PreviewMode);
        Assert.All(source.Definitions, item => Assert.Equal("tasks-ui", item.ModuleId));
        Assert.Contains(source.Definitions, item => item.Id == "ui.text.emptyAllTitle");
        Assert.Contains(source.Definitions, item => item.Id == "ui.text.restoredSuccess");
        Assert.Contains(source.Definitions, item => item.Id == "tasks.button.minHeight");
        await module.StopAsync();
        Assert.Empty(settings.Sources);
    });

    [Fact]
    public Task Task_page_created_after_preview_replays_runtime_values_and_discard_replays_saved_values() => Sta.Run(async () =>
    {
        var repository = new TestSettingsRepository();
        await using var settings = new SettingsModule(new(repository));
        await using var tasks = new TasksUiModule(new(new Api(), new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics(), settings.Api));
        await tasks.ActivateAsync();
        var model = CreateSettingsViewModel(settings.Api, new Dialogs()); await Initialize(model);
        try
        {
            Set(model, "ShowAdvanced", true);
            var row = VisibleRows(model).Cast<object>().Single(item => Get<string>(item, "Id") == "tasks.layout.editorPaneWidth");
            Set(row, "TextValue", "600");
            await (Task)model.GetType().GetMethod("DrainPreviewAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(model, null)!;
            Assert.Equal(0, repository.Writes);
            var first = tasks.Views[0].Factory.Create(); await first.Controller.InitializeAsync();
            Assert.Equal(600, Get<double>(first.View.DataContext, "EditorPaneWidth")); await first.Controller.DisposeAsync();
            await Get<IAsyncRelayCommand>(model, "DiscardCommand").ExecuteAsync(null);
            var second = tasks.Views[0].Factory.Create(); await second.Controller.InitializeAsync();
            Assert.Equal(480, Get<double>(second.View.DataContext, "EditorPaneWidth")); await second.Controller.DisposeAsync();
        }
        finally { await ((IPageController)model).DisposeAsync(); }
    });

    [Fact]
    public Task Settings_editors_reflow_and_save_bar_remains_outside_the_scroll_area() => Sta.Run(async () =>
    {
        var api = new SettingsApi { Definitions = new ShellSettingSource().Definitions };
        var module = new SettingsUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        var page = Assert.Single(module.Views).Factory.Create();
        page.View.Resources.MergedDictionaries.Add(LoadTheme());
        try
        {
            await page.Controller.InitializeAsync();
            var model = page.View.DataContext;
            var row = VisibleRows(model)[0]!;
            var actionBar = Assert.IsType<Border>(page.View.FindName("SettingsActionBar"));
            page.View.Measure(new Size(760, 620));
            page.View.Arrange(new Rect(0, 0, 760, 620));
            page.View.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, actionBar.Visibility);
            Set(row, "ChoiceValue", "Dark");
            Assert.Equal(Visibility.Visible, actionBar.Visibility);
            foreach (var width in new[] { 760d, 1100d })
            {
                page.View.Measure(new Size(width, 620));
                page.View.Arrange(new Rect(0, 0, width, 620));
                page.View.UpdateLayout();
                Assert.Equal(width < 900, Get<bool>(page.View, "IsCompact"));
                var footer = Assert.IsType<Border>(page.View.FindName("SettingsActionBar"));
                var scroll = Assert.IsType<ScrollViewer>(page.View.FindName("SettingsScrollViewer"));
                Assert.True(footer.ActualHeight >= 64);
                Assert.True(footer.TranslatePoint(new Point(0, footer.ActualHeight), page.View).Y <= 620.5);
                Assert.DoesNotContain(footer, FindVisualChildren<Border>(scroll));
                Assert.Equal(2, FindVisualChildren<ComboBox>(page.View).Count());
                foreach (var combo in FindVisualChildren<ComboBox>(page.View))
                {
                    var position = combo.TranslatePoint(new Point(), page.View);
                    Assert.True(position.X >= 0 && position.X + combo.ActualWidth <= width + 0.5);
                    Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(combo)));
                }
            }
            Assert.StartsWith("1 项修改尚未保存", Get<string>(model, "ChangesLabel"));
            await Get<IAsyncRelayCommand>(model, "DiscardCommand").ExecuteAsync(null);
            Assert.Equal(Visibility.Collapsed, actionBar.Visibility);
            Set(row, "ChoiceValue", "Light");
            Assert.Equal(Visibility.Visible, actionBar.Visibility);
            await Get<IAsyncRelayCommand>(model, "SaveCommand").ExecuteAsync(null);
            Assert.Equal(Visibility.Collapsed, actionBar.Visibility);
        }
        finally
        {
            await page.Controller.DisposeAsync();
            await module.DisposeAsync();
        }
    });

    [Fact]
    public Task Normal_settings_remain_editable_and_advanced_mode_keeps_the_same_rows() => Sta.Run(async () =>
    {
        var api = new SettingsApi { Definitions = new ShellSettingSource().Definitions };
        var model = CreateSettingsViewModel(api, new Dialogs());
        await Initialize(model);
        var row = VisibleRows(model)[0]!;
        Set(row, "ChoiceValue", "Dark");
        Assert.True(Get<IAsyncRelayCommand>(model, "SaveCommand").CanExecute(null));
        Set(model, "ShowAdvanced", true);
        Assert.Contains(row, VisibleRows(model).Cast<object>());
        Set(model, "ShowAdvanced", false);
        Assert.Same(row, VisibleRows(model)[0]);
        Assert.Equal("Dark", Get<string>(row, "ChoiceValue"));
        await Get<IAsyncRelayCommand>(model, "SaveCommand").ExecuteAsync(null);
        Assert.Single(Assert.Single(api.SaveCalls));
        await ((IPageController)model).DisposeAsync();
    });

    [Fact]
    public Task Advanced_dirty_input_survives_mode_and_section_changes_without_a_prompt() => Sta.Run(async () =>
    {
        var dialogs = new Dialogs();
        var api = new SettingsApi { Definitions = new ShellSettingSource().Definitions.ToArray() };
        var model = CreateSettingsViewModel(api, dialogs); await Initialize(model);
        Set(model, "ShowAdvanced", true);
        var row = VisibleRows(model).Cast<object>().Single(item => Get<string>(item, "Id") == "shell.sidebar.fullWidth");
        Set(row, "TextValue", "200"); Set(model, "ShowAdvanced", false);
        Assert.False(Get<bool>(model, "ShowAdvanced")); Assert.Equal(0, dialogs.UnsavedPrompts);
        Assert.True(Get<bool>(model, "HasChanges"));
        Set(model, "ShowAdvanced", true);
        var appearance = Enum.Parse(Get<object>(model, "SelectedSection").GetType(), "Appearance");
        await Get<IAsyncRelayCommand>(model, "SelectSectionCommand").ExecuteAsync(appearance);
        Assert.Equal("Appearance", Get<object>(model, "SelectedSection").ToString());
        Assert.Equal("200", Get<string>(row, "TextValue")); Assert.Equal(0, dialogs.UnsavedPrompts);
        Assert.Contains("1", Get<string>(model, "ChangesLabel"));
        await ((IPageController)model).DisposeAsync();
    });

    [Fact]
    public Task Section_reset_stages_defaults_without_writing_until_save() => Sta.Run(async () =>
    {
        var api = new SettingsApi { Definitions = new ShellSettingSource().Definitions,
            Values = new Dictionary<string, object?> { ["appearance.theme"] = "Dark", ["ui.language"] = "English" } };
        var model = CreateSettingsViewModel(api, new Dialogs()); await Initialize(model);
        await Get<IAsyncRelayCommand>(model, "ResetSectionCommand").ExecuteAsync(null);
        Assert.Empty(api.ResetCalls); Assert.Empty(api.SaveCalls);
        Assert.True(Get<bool>(model, "HasChanges"));
        await Get<IAsyncRelayCommand>(model, "SaveCommand").ExecuteAsync(null);
        Assert.Equal(new[] { "appearance.theme", "ui.language" }, Assert.Single(api.SaveCalls).Keys.Order().ToArray());
        await ((IPageController)model).DisposeAsync();
    });

    [Fact]
    public Task Appearance_section_lists_theme_language_and_shell_layout_rows() => Sta.Run(async () =>
    {
        var api = new SettingsApi
        {
            Definitions = new ShellSettingSource().Definitions.ToArray()
        };
        var model = CreateSettingsViewModel(api, new Dialogs());
        await Initialize(model);
        SetProperty(model, "ShowAdvanced", true);
        SetProperty(model, "SelectedSection", Enum.Parse(Get<object>(model, "SelectedSection").GetType(), "Appearance"));
        var ids = RowIds(model);
        Assert.Equal(10, ids.Count);
        Assert.Contains("appearance.theme", ids);
        Assert.Contains("ui.language", ids);
        Assert.Contains("shell.sidebar.fullWidth", ids);
        Assert.DoesNotContain("shell.window.minWidth", ids);
        Assert.DoesNotContain("tasks.confirmDelete", ids);
        Assert.DoesNotContain("startup.width", ids);
    });

    [Fact]
    public Task Show_advanced_reveals_all_rows_in_current_section_and_notifies_visibility() => Sta.Run(async () =>
    {
        var api = new SettingsApi
        {
            Definitions = new ShellSettingSource().Definitions.ToArray()
        };
        var model = CreateSettingsViewModel(api, new Dialogs());
        var notified = new List<string>();
        ((INotifyPropertyChanged)model).PropertyChanged += (_, e) => notified.Add(e.PropertyName!);
        await Initialize(model);
        Assert.Equal(2, RowIds(model).Count);
        Assert.Equal("Appearance", Get<object>(model, "SelectedSection").ToString());
        Assert.Single(Get<IList>(model, "Sections"));

        SetProperty(model, "SelectedSection", Enum.Parse(Get<object>(model, "SelectedSection").GetType(), "Appearance"));
        SetProperty(model, "ShowAdvanced", true);
        Assert.Equal(10, RowIds(model).Count);
        Assert.Contains("HasVisibleRows", notified);
        Assert.Contains("HasRows", notified);
        Assert.Contains("EmptyMessage", notified);
    });
}
