using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Reflection;
using System.Linq.Expressions;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using Mosaic.Contracts;
using Mosaic.DesktopPresence.Contracts;
using Mosaic.DesktopPresence.Wpf;
using Mosaic.FloatingWindows.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Core;
using Mosaic.Settings.Wpf;
using Mosaic.Shell.Wpf;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Wpf;

namespace Mosaic.Wpf.Tests;

public sealed class PresenceTests
{
    private sealed class Diagnostics : IDiagnosticSink
    {
        public void Report(string correlationId, Exception exception) { }
    }
    private sealed class Dialogs : IUserDialogService
    {
        public Task<bool> ConfirmDeleteAsync(string title) => Task.FromResult(false);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<UnsavedChoice> AskUnsavedChangesAsync() => Task.FromResult(UnsavedChoice.Cancel);
        public Task ShowErrorAsync(string message) => Task.CompletedTask;
    }
    private sealed class Host : IDesktopWindowHost
    {
        public MainWindowPresentation CurrentPresentation => MainWindowPresentation.Visible;
#pragma warning disable CS0067
        public event EventHandler? PresentationChanged;
#pragma warning restore CS0067
        public Task HideMainWindowAsync() => Task.CompletedTask;
        public Task RestoreMainWindowAsync() => Task.CompletedTask;
        public Task<ExitRequestResult> RequestApplicationExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(ExitRequestResult.Cancelled);
    }
    private sealed class Repository : ISettingRepository
    {
        private readonly Dictionary<string, object?> _values = [];
        private readonly HashSet<string> _migrations = [];
        internal int PendingInitializeFailures;
        internal int Initializations;
        public Task<OperationResult<IReadOnlyDictionary<string, object?>>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, object?>>.Success(new Dictionary<string, object?>(_values)));
        public Task<OperationResult> SaveAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
        { foreach (var item in values) _values[item.Key] = item.Value; return Task.FromResult(OperationResult.Success()); }
        public Task<OperationResult> DeleteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
        { foreach (var id in ids) _values.Remove(id); return Task.FromResult(OperationResult.Success()); }
        // 与 SQLite 实现一致：迁移 ID 只生效一次，已有值不覆盖，失败可重试。
        public Task<OperationResult> InitializeAsync(SettingInitialization initialization, CancellationToken cancellationToken = default)
        {
            Initializations++;
            if (PendingInitializeFailures > 0)
            {
                PendingInitializeFailures--;
                return Task.FromResult(OperationResult.Failure(new(ErrorCodes.StorageUnavailable, "初始化暂时不可用")));
            }
            if (_migrations.Add(initialization.MigrationId)) _values.TryAdd(initialization.SettingId, initialization.Value);
            return Task.FromResult(OperationResult.Success());
        }
    }
    private sealed class EmptyFactory : ICompanionViewFactory
    {
        public ICompanionViewSession Create(CompanionViewOptions options) => throw new InvalidOperationException("Not opened by this test");
    }
    private sealed class Factory : ICompanionViewFactory
    {
        internal List<Session> Sessions = [];
        public ICompanionViewSession Create(CompanionViewOptions options)
        {
            var session = new Session(options); Sessions.Add(session); return session;
        }
    }
    private sealed class Session(CompanionViewOptions options) : ICompanionViewSession
    {
        internal CompanionViewOptions Options => options;
        internal bool Closed;
        internal bool Disposed;
        public FrameworkElement View { get; } = new TextBlock { Text = "只读任务摘要 · 未完成 / 已完成", TextWrapping = TextWrapping.Wrap };
        public event EventHandler? LayoutInvalidated;
        internal void UpdateText(string text) { ((TextBlock)View).Text = text; LayoutInvalidated?.Invoke(this, EventArgs.Empty); }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync() { Closed = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Api : ITasksApi
    {
        internal Func<TaskQuery, CancellationToken, Task<OperationResult<IReadOnlyList<TaskItemDto>>>> Query = (_, _) =>
            Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([]));
        private IReadOnlyList<TaskItemDto>? _lastList;
        public async Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default)
        {
            var result = await Query(query, cancellationToken);
            if (result.IsSuccess) _lastList = result.Value;
            return result;
        }
        public Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        internal Func<Guid, CancellationToken, Task<OperationResult<TaskItemDto>>> Get = (_, _) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> GetAsync(Guid id, CancellationToken cancellationToken = default) => Get(id, cancellationToken);
        public Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        internal Func<SetTaskCompletedRequest, CancellationToken, Task<OperationResult<TaskItemDto>>> Complete = (_, _) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request, CancellationToken cancellationToken = default) => Complete(request, cancellationToken);
        public Task<OperationResult<TaskItemDto>> SetArchivedAsync(SetTaskArchivedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskConstraintsDto>> GetConstraintsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    
        internal Func<TaskQuery, CancellationToken, Task<OperationResult<TaskCountsDto>>>? Counts;
        public Task<OperationResult<TaskCountsDto>> CountAsync(TaskQuery query, CancellationToken cancellationToken = default) =>
            Counts is { } counts
                ? counts(query, cancellationToken)
                : Task.FromResult(OperationResult<TaskCountsDto>.Success(new(_lastList?.Count ?? 0, _lastList?.Count(task => !task.IsCompleted) ?? 0)));
}
    [Fact]
    public Task Peek_whole_label_routes_title_attachment_padding_and_buttons_but_filter_does_not() => Sta.Run(async () =>
    {
        var item = Item("整卡标题") with { AttachmentCount = 1 };
        var api = new Api { Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([item])) };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics())); await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme")); await session.InitializeAsync(); Texts(session.View);
        var view = session.View; var type = view.GetType();
        type.GetField("CanDetach", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, (Func<bool>)(() => true));
        var card = Descendants(view).OfType<Button>().Single(button => button.Name == "TaskCard");
        var row = (FrameworkElement)card.Parent;
        var title = Descendants(card).OfType<TextBlock>().Single(text => text.Text == "整卡标题");
        var badge = Descendants(card).OfType<Border>().Single(border => border.Name == "AttachmentBadge");
        var cancel = type.GetMethod("CancelDrag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var candidate = type.GetField("_candidate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var origin in new UIElement[] { title, badge.Child, card, Completion(view) })
        {
            origin.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            Assert.Same(row, candidate.GetValue(view)); cancel.Invoke(view, null);
        }
        foreach (var origin in new UIElement[] { Filter(view, "All") })
        {
            origin.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            Assert.Null(candidate.GetValue(view));
        }
    });

    [Fact]
    public Task Peek_summary_and_filter_segments_stay_inside_narrow_layouts() => Sta.Run(async () =>
    {
        var api = new Api { Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([Item("窄布局任务")])) };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics())); await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme")); await session.InitializeAsync(); Texts(session.View);
        session.View.Measure(new Size(236, 420)); session.View.Arrange(new Rect(0, 0, 236, 420)); session.View.UpdateLayout();
        var filters = (FrameworkElement)session.View.FindName("Filters");
        Assert.Equal(Visibility.Visible, filters.Visibility);
        var right = filters.TranslatePoint(new Point(filters.ActualWidth, 0), session.View).X;
        Assert.InRange(right, 1, 236);
        Assert.InRange(filters.ActualWidth, 120, 236);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Peek_keeps_attachment_badges_right_aligned_with_or_without_completion_button(bool showCompletionToggle) => Sta.Run(async () =>
    {
        var open = Item("附有资料的长标题任务，窄布局仍要预留标题空间") with { AttachmentCount = 2 };
        var done = Item("已完成任务", true);
        IReadOnlyList<TaskItemDto> items = [open, done];
        var api = new Api { Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success(items)) };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme", ShowCompletionToggle: showCompletionToggle));
        var view = session.View;
        IReadOnlyList<Button> Cards()
        {
            view.Measure(new Size(236, 420));
            view.Arrange(new Rect(0, 0, 236, 420));
            view.UpdateLayout();
            return Descendants(view).OfType<Button>().Where(button => button.Name == "TaskCard").ToArray();
        }
        static Border Badge(Button card) => Assert.Single(Descendants(card).OfType<Border>(), border => border.Name == "AttachmentBadge");
        void AssertBadgePosition(Button card)
        {
            var badge = Badge(card);
            var content = Assert.IsType<Grid>(card.Content);
            var badgeRight = badge.TranslatePoint(new Point(badge.ActualWidth, 0), content).X;
            Assert.InRange(content.ActualWidth, 1, 236);
            Assert.Equal(content.ActualWidth, badgeRight, precision: 1);
            Assert.DoesNotContain(Descendants(card).OfType<TextBlock>(), text => text.Text is "未完成" or "已完成");
        }

        await session.InitializeAsync();
        var cards = Cards();
        Assert.Equal(2, cards.Count);
        var badge = Badge(cards[0]);
        var countLabel = Mosaic.Tasks.Wpf.Internal.TaskLocalization.AttachmentCountLabel(2);
        Assert.Equal(Visibility.Visible, badge.Visibility);
        Assert.Equal(countLabel, badge.ToolTip);
        Assert.Contains(countLabel, System.Windows.Automation.AutomationProperties.GetName(cards[0]));
        Assert.Equal(Visibility.Collapsed, Badge(cards[1]).Visibility);
        AssertBadgePosition(cards[0]);
        var title = Assert.Single(Descendants(cards[0]).OfType<TextBlock>(), text => text.Text == open.Title);
        Assert.True(title.ActualWidth > 0);

        items = [open with { AttachmentCount = 0 }, done with { AttachmentCount = 1 }];
        await session.InitializeAsync();
        cards = Cards();
        Assert.Equal(Visibility.Collapsed, Badge(cards[0]).Visibility);
        Assert.Equal(Visibility.Visible, Badge(cards[1]).Visibility);
        AssertBadgePosition(cards[1]);
    });

    [Fact]
    public Task Peek_drag_forwards_placement_preview_and_ends_on_cancel() => Sta.Run(async () =>
    {
        var api = new Api { Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([Item("预览任务")])) };
        var floating = new FloatingApi();
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics())); await module.ActivateAsync();
        using var registration = module.DetachSource.RegisterDetachHandler(floating);
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme")); await session.InitializeAsync(); Texts(session.View);
        var view = session.View; var type = view.GetType();
        var host = new Window { Content = view, Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Show(); view.UpdateLayout();
        try
        {
            var card = Descendants(view).OfType<Button>().Single(button => button.Name == "TaskCard");
            var row = (FrameworkElement)card.Parent;
            type.GetField("_candidate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, row);
            type.GetField("_start", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, new Point(10, 10));
            type.GetField("_grab", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, new Point(10, 10));
            type.GetMethod("ActivateDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [new Point(60, 60)]);
            Assert.Equal(1, floating.PreviewBegins); Assert.NotNull(floating.Hint); Assert.Equal(10, floating.Hint!.GrabX);
            type.GetMethod("MovePreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [new Point(80, 70)]);
            Assert.Equal(1, floating.PreviewUpdates);
            type.GetMethod("CancelDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            Assert.Equal(1, floating.PreviewEnds);
        }
        finally { host.Close(); }
    });

    private sealed class FloatingApi : IFloatingWindowApi
    {
        internal int PreviewBegins, PreviewUpdates, PreviewEnds;
        internal FloatingPlacementHint? Hint;
        internal Func<FloatingContentReference, FloatingPlacementHint?, CancellationToken, Task<OperationResult<FloatingOpenResult>>>? Open;
        public Task<OperationResult<FloatingOpenResult>> OpenAsync(FloatingContentReference reference, FloatingPlacementHint? hint = null, CancellationToken cancellationToken = default) =>
            Open?.Invoke(reference, hint, cancellationToken) ?? Task.FromResult(OperationResult<FloatingOpenResult>.Success(new(FloatingOpenDisposition.Created)));
        public Task<OperationResult> CloseAsync(FloatingContentReference reference, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
        public void BeginPlacementPreview(FloatingContentReference reference, FloatingPlacementHint hint) { PreviewBegins++; Hint = hint; }
        public void UpdatePlacementPreview(FloatingPlacementHint hint) { PreviewUpdates++; Hint = hint; }
        public void EndPlacementPreview() => PreviewEnds++;
    }

    private static Button Completion(FrameworkElement view) => Descendants(view).OfType<Button>().Single(button => button.Name == "CompleteButton");
    private static RadioButton Filter(FrameworkElement view, string name) => Descendants(view).OfType<RadioButton>().Single(button => button.Tag as string == name);
    private static async Task Drain() { for (var i = 0; i < 5; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }

    [Fact]
    public Task Peek_completion_uses_revision_prevents_duplicates_and_requeries_current_filter() => Sta.Run(async () =>
    {
        var item = Item("可完成任务") with { Revision = 7 };
        var queries = new List<TaskFilter>(); var writes = 0;
        var pending = new TaskCompletionSource<OperationResult<TaskItemDto>>();
        var api = new Api {
            Query = (q, _) => { queries.Add(q.Filter); return Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success(q.Filter == TaskFilter.Open && item.IsCompleted ? [] : [item])); },
            Complete = (request, _) => { writes++; Assert.Equal(item.Id, request.Id); Assert.Equal(7, request.ExpectedRevision); Assert.True(request.IsCompleted); return pending.Task; }
        };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync(); await using var session = module.CompanionFactory.Create(new("Open", 12, "Theme", "Theme"));
        await session.InitializeAsync(); Texts(session.View);
        var button = Completion(session.View);
        Assert.Contains("标记完成", System.Windows.Automation.AutomationProperties.GetName(button));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(button.IsEnabled); Assert.True(((ICompanionInteraction)session).IsInteracting);
        Assert.Contains("处理中", System.Windows.Automation.AutomationProperties.GetName(button));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(1, writes);
        item = item with { IsCompleted = true, Revision = 8 }; pending.SetResult(OperationResult<TaskItemDto>.Success(item)); await Drain();
        Assert.False(((ICompanionInteraction)session).IsInteracting); Assert.All(queries, filter => Assert.Equal(TaskFilter.Open, filter));
        Assert.Contains("没有符合筛选的任务", Texts(session.View));
        Filter(session.View, "Completed").RaiseEvent(new RoutedEventArgs(RadioButton.CheckedEvent)); await Drain();
        Assert.Equal(TaskFilter.Completed, queries.Last()); Assert.Contains("恢复未完成", System.Windows.Automation.AutomationProperties.GetName(Completion(session.View)));
        Assert.False(((ICompanionNavigation)session).TryGoBack());
    });

    [Fact]
    public Task Peek_drop_releases_capture_before_open_and_requests_dismiss_only_on_success() => Sta.Run(async () =>
    {
        var first = Item("拖出回归一");
        var second = Item("拖出回归二");
        var requests = new List<string>();
        var dismissCount = 0;
        var firstOpen = new TaskCompletionSource<OperationResult<FloatingOpenResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var floating = new FloatingApi
        {
            Open = (reference, _, _) =>
            {
                requests.Add(reference.ItemKey);
                return requests.Count == 1 ? firstOpen.Task : Task.FromResult(OperationResult<FloatingOpenResult>.Success(new(FloatingOpenDisposition.Created)));
            }
        };
        var api = new Api { Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([first, second])) };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        using var registration = module.DetachSource.RegisterDetachHandler(floating);
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme"));
        session.DismissRequested += (_, _) => dismissCount++;
        await session.InitializeAsync(); Texts(session.View);
        var view = session.View; var type = view.GetType();
        var interaction = (ICompanionInteraction)session;
        var candidate = type.GetField("_candidate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var activate = type.GetMethod("ActivateDrag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var finish = type.GetMethod("FinishDrag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var host = new Window { Content = view, Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Show();
        host.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative) });
        view.UpdateLayout();
        var rows = Descendants(view).OfType<Button>().Where(button => button.Name == "TaskCard").Select(button => (FrameworkElement)button.Parent).ToArray();
        Task Drop(FrameworkElement row, Point windowPosition) => (Task)finish.Invoke(view, [row, windowPosition, windowPosition])!;
        void Start(FrameworkElement row)
        {
            candidate.SetValue(view, row);
            activate.Invoke(view, [new Point(60, 60)]);
            Assert.True(row.IsMouseCaptured);
            Assert.True(interaction.IsInteracting);
        }
        try
        {
            Start(rows[0]);
            Assert.Null(Descendants(view).OfType<Popup>().FirstOrDefault());
            var layer = AdornerLayer.GetAdornerLayer(view);
            Assert.NotNull(layer);
            Assert.Contains(layer.GetAdorners(view) ?? [], adorner => adorner.GetType().Name == "DragHintAdorner");
            var pending = Drop(rows[0], new Point(-500, -500));
            Assert.False(pending.IsCompleted);
            Assert.False(rows[0].IsMouseCaptured);
            Assert.Empty(layer.GetAdorners(view) ?? []);
            Assert.True(interaction.IsInteracting);
            Assert.Equal(0, dismissCount);
            firstOpen.SetResult(OperationResult<FloatingOpenResult>.Failure(new(ErrorCodes.CapabilityUnavailable, "无法打开")));
            await pending;
            Assert.False(interaction.IsInteracting);
            Assert.Equal(0, dismissCount);

            Start(rows[1]);
            await Drop(rows[1], new Point(60, 60));
            Assert.False(rows[1].IsMouseCaptured);
            Assert.Equal(0, dismissCount);
            Start(rows[1]);
            await Drop(rows[1], new Point(-500, -500));
            Assert.False(rows[1].IsMouseCaptured);
            Assert.False(interaction.IsInteracting);
            Assert.Equal(1, dismissCount);
            Assert.Equal([first.Id.ToString("D"), second.Id.ToString("D")], requests);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task Peek_repeated_drags_never_stack_drag_hint_adorners() => Sta.Run(async () =>
    {
        // 拖动未到桌面即松手（MouseUp 丢失或行复用离树）后再次拖动，
        // 虚线提示层必须被清理而不是随字段覆盖在 AdornerLayer 里逐次叠加残留。
        var first = Item("拖拽残留一");
        var second = Item("拖拽残留二");
        var api = new Api { Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([first, second])) };
        var floating = new FloatingApi();
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        using var registration = module.DetachSource.RegisterDetachHandler(floating);
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme"));
        await session.InitializeAsync(); Texts(session.View);
        var view = session.View; var type = view.GetType();
        var interaction = (ICompanionInteraction)session;
        var candidate = type.GetField("_candidate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var activate = type.GetMethod("ActivateDrag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var move = type.GetMethod("MoveGesture", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var host = new Window { Content = view, Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Show(); view.UpdateLayout();
        var layer = AdornerLayer.GetAdornerLayer(view);
        Assert.NotNull(layer);
        int Hints() => (layer.GetAdorners(view) ?? []).Count(adorner => adorner.GetType().Name == "DragHintAdorner");
        try
        {
            var rows = Descendants(view).OfType<Button>().Where(button => button.Name == "TaskCard").Select(button => (FrameworkElement)button.Parent).ToArray();
            Assert.Equal(2, rows.Length);

            // 第一次拖动：激活后 MouseUp 丢失，靠后续 Move（左键已松开）察觉并清理。
            candidate.SetValue(view, rows[0]);
            activate.Invoke(view, [new Point(60, 60)]);
            Assert.Equal(1, Hints());
            move.Invoke(view, [rows[0], new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.PreviewMouseMoveEvent }]);
            Assert.Equal(0, Hints());
            Assert.Null(candidate.GetValue(view));
            Assert.False(rows[0].IsMouseCaptured);
            Assert.False(interaction.IsInteracting);

            // 泄漏场景：同一行连续两次激活之间不经过任何清理路径（模拟事件全部丢失），
            // 第二次 ActivateDrag 必须先清扫旧提示层，AdornerLayer 里只留最新一个。
            candidate.SetValue(view, rows[0]);
            activate.Invoke(view, [new Point(60, 60)]);
            Assert.Equal(1, Hints());
            candidate.SetValue(view, rows[0]);
            activate.Invoke(view, [new Point(70, 70)]);
            Assert.Equal(1, Hints());
            Assert.True(rows[0].IsMouseCaptured);

            // 弹窗内松手（CancelDrag）后 AdornerLayer 不得残留任何提示层。
            type.GetMethod("CancelDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            Assert.Equal(0, Hints());
            Assert.False(interaction.IsInteracting);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task Peek_conflict_refreshes_revision_failure_does_not_flip_and_message_can_dismiss() => Sta.Run(async () =>
    {
        var item = Item("冲突任务"); var writes = 0;
        var api = new Api {
            Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([item])),
            Complete = (r, _) => { writes++; if (writes == 1) { item = item with { Revision = 9 }; return Task.FromResult(OperationResult<TaskItemDto>.Failure(new(ErrorCodes.Conflict, "Conflict", MessageKey: "Tasks.Error.Conflict"))); } Assert.Equal(9, r.ExpectedRevision); return Task.FromResult(OperationResult<TaskItemDto>.Failure(new(ErrorCodes.StorageUnavailable, "Storage unavailable"))); }
        };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync(); await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme"));
        await session.InitializeAsync(); Texts(session.View);
        Completion(session.View).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
        Assert.Contains(Texts(session.View), text => text.Contains("重新加载")); Assert.False(item.IsCompleted);
        Completion(session.View).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain(); Assert.Equal(2, writes);
        var dismiss = Descendants(session.View).OfType<Button>().Single(button => button.Name == "DismissFeedback");
        dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)session.View.FindName("OperationFeedback")).Visibility);
    });

    [Fact]
    public Task Peek_filter_cancels_old_query_ignores_late_results_and_new_session_uses_default() => Sta.Run(async () =>
    {
        var late = new TaskCompletionSource<OperationResult<IReadOnlyList<TaskItemDto>>>(); CancellationToken token = default;
        var queries = new List<TaskFilter>();
        var api = new Api { Query = (q, ct) => { queries.Add(q.Filter); if (queries.Count == 1) { token = ct; return late.Task; } return Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([Item("最新列表")])); } };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics())); await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme"));
        var initial = session.InitializeAsync(); Texts(session.View);
        Filter(session.View, "Completed").RaiseEvent(new RoutedEventArgs(RadioButton.CheckedEvent)); await Drain(); Assert.True(token.IsCancellationRequested);
        late.SetResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([Item("旧列表")])); await initial;
        Assert.Contains("最新列表", Texts(session.View)); Assert.DoesNotContain("旧列表", Texts(session.View));
        await using var next = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme", ShowCompletionToggle: false, ShowFilterSwitch: false));
        await next.InitializeAsync(); Texts(next.View); Assert.Equal(TaskFilter.All, queries.Last());
        Assert.Equal(Visibility.Collapsed, Completion(next.View).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)next.View.FindName("Filters")).Visibility);
    });

    [Fact]
    public Task Peek_reuses_row_containers_when_a_refresh_returns_the_same_items() => Sta.Run(async () =>
    {
        // 视图每次刷新都重建行会让焦点和容器身份丢失；同一批任务刷新时必须复用行与容器，
        // 而且程序同步筛选按钮的选中状态不能反过来再触发一次查询。
        var item = Item("行复用");
        var queries = new List<TaskFilter>();
        var api = new Api
        {
            Query = (q, _) =>
            {
                queries.Add(q.Filter);
                return Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([item]));
            }
        };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme"));
        await session.InitializeAsync(); Texts(session.View);
        Button Card() => Descendants(session.View).OfType<Button>().Single(button => button.Name == "TaskCard");
        var card = Card();
        var row = card.DataContext;

        Filter(session.View, "Open").RaiseEvent(new RoutedEventArgs(RadioButton.CheckedEvent)); await Drain();

        Assert.Equal(new[] { TaskFilter.All, TaskFilter.Open }, queries);
        var after = Card();
        Assert.Same(card, after);
        Assert.Same(row, after.DataContext);
        Assert.Contains("行复用", Texts(session.View));
    });

    [Fact]
    public Task Peek_fetches_one_page_without_notes_and_reads_its_counts_from_the_aggregate() => Sta.Run(async () =>
    {
        // 预览只渲染 MaxItems 行：查询必须带分页并跳过备注，总数与未完成数来自一次聚合计数，
        // 而不是把整张筛选结果取回来再在界面上截断。
        var item = Item("分页任务");
        TaskQuery? listed = null; TaskQuery? counted = null;
        var api = new Api
        {
            Query = (query, _) =>
            {
                listed = query;
                return Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([item]));
            },
            Counts = (query, _) =>
            {
                counted = query;
                return Task.FromResult(OperationResult<TaskCountsDto>.Success(new(37, 12)));
            }
        };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme"));
        await session.InitializeAsync(); Texts(session.View);

        Assert.Equal(12, listed!.Limit);
        Assert.False(listed.IncludeNotes);
        Assert.Equal(TaskFilter.All, counted!.Filter);
        Assert.Contains("共 37 项 · 12 项未完成", Texts(session.View));
    });

    [Fact]
    public Task Peek_close_cancels_pending_write_and_discards_late_result() => Sta.Run(async () =>
    {
        var pending = new TaskCompletionSource<OperationResult<TaskItemDto>>(); CancellationToken token = default;
        var item = Item("关闭测试"); var queries = 0;
        var api = new Api { Query = (_, _) => { queries++; return Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([item])); }, Complete = (_, ct) => { token = ct; return pending.Task; } };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics())); await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme")); await session.InitializeAsync(); Texts(session.View);
        Completion(session.View).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await session.CloseAsync(); Assert.True(token.IsCancellationRequested);
        pending.SetResult(OperationResult<TaskItemDto>.Success(item with { IsCompleted = true })); await Drain(); Assert.Equal(1, queries);
    });

    private static TaskItemDto Item(string title, bool completed = false) => new(Guid.NewGuid(), title, "", completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
    private static string[] Texts(FrameworkElement view)
    {
        view.Measure(new Size(380, 560)); view.Arrange(new Rect(0, 0, 380, 560)); view.UpdateLayout();
        return Descendants(view).OfType<TextBlock>().Select(text => text.Text).ToArray();
    }

    [Fact]
    public Task Details_use_stable_identity_ignore_late_results_and_return_without_edits() => Sta.Run(async () =>
    {
        var a = Item("同名任务"); var b = Item("同名任务") with { Notes = "B full notes" };
        var late = new TaskCompletionSource<OperationResult<TaskItemDto>>();
        CancellationToken aToken = default;
        var api = new Api
        {
            Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([a,b])),
            Get = (id, ct) => { if (id == a.Id) { aToken = ct; return late.Task; } Assert.Equal(b.Id,id); return Task.FromResult(OperationResult<TaskItemDto>.Success(b)); }
        };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("All",12,"Theme","Theme"));
        await session.InitializeAsync(); Texts(session.View);
        var rows = Descendants(session.View).OfType<Button>().Where(button => button.DataContext?.GetType().Name == "TaskPeekRow" && button.Name == "TaskCard").ToArray();
        Assert.Equal(2, rows.Length);
        rows[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        rows[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(aToken.IsCancellationRequested);
        late.SetResult(OperationResult<TaskItemDto>.Success(a with { Notes = "stale A notes" }));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Texts(session.View);
        var fields = Descendants(session.View).OfType<TextBox>().ToArray();
        Assert.Contains(fields, field => field.Text == "B full notes");
        Assert.All(fields, field => Assert.True(field.IsReadOnly));
        Assert.DoesNotContain(fields, field => field.Text == "stale A notes");
        Assert.True(((ICompanionNavigation)session).TryGoBack());
        Assert.False(((ICompanionNavigation)session).TryGoBack());
        await session.CloseAsync();
    });

    [Fact]
    public Task Deleted_details_can_retry_and_close_while_query_is_pending() => Sta.Run(async () =>
    {
        var item = Item("Deleted"); var pending = new TaskCompletionSource<OperationResult<TaskItemDto>>();
        var calls = 0; CancellationToken token = default;
        var api = new Api { Query = (_,_) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([item])),
            Get = (_, ct) => { if (++calls == 1) return Task.FromResult(OperationResult<TaskItemDto>.Failure(new(ErrorCodes.NotFound,"gone"))); token = ct; return pending.Task; } };
        await using var module = new TasksUiModule(new(api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Dialogs(),new Diagnostics()));
        await module.ActivateAsync(); await using var session = module.CompanionFactory.Create(new("All",12,"Theme","Theme"));
        await session.InitializeAsync(); Texts(session.View);
        Descendants(session.View).OfType<Button>().Single(button => button.DataContext?.GetType().Name == "TaskPeekRow" && button.Name == "TaskCard").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Texts(session.View);
        Assert.Contains(Descendants(session.View).OfType<TextBlock>(), text => text.Text.Contains("删除"));
        var retry = Descendants(session.View).OfType<Button>().Last(button => button.Content as string == "重试");
        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await session.CloseAsync(); Assert.True(token.IsCancellationRequested);
        pending.SetResult(OperationResult<TaskItemDto>.Success(item));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(((ICompanionNavigation)session).TryGoBack());
    });

    [Fact]
    public Task Settings_register_28_rows_project_6_normal_validate_save_and_revoke() => Sta.Run(async () =>
    {
        const string legacyStatusText = "desktop.popup.showStatusText";
        var repository = new Repository();
        await repository.SaveAsync(new Dictionary<string, object?> { [legacyStatusText] = true, ["desktop.popup.titleFontSize"] = 16 });
        await using var settings = new SettingsModule(new(repository)); await settings.ActivateAsync();
        await using var module = new DesktopPresenceModule(new(new Host(), new EmptyFactory(), settings.Api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Diagnostics()));
        await module.ActivateAsync();
        var definitions = (await settings.Api.ListDefinitionsAsync()).Value;
        Assert.Equal(29, definitions.Count);
        Assert.Equal(28, definitions.Count(item => item.ShowInSettings));
        Assert.DoesNotContain(definitions, item => item.Id == legacyStatusText);
        var lastFilter = Assert.Single(definitions, item => item.Id == "desktop.popup.lastFilter");
        Assert.False(lastFilter.ShowInSettings);
        Assert.Equal(SettingPreviewMode.None, lastFilter.PreviewMode);
        Assert.Equal("Unset", lastFilter.DefaultValue);
        Assert.Equal(["Unset", "All", "Open", "Completed"], lastFilter.Options!.Select(option => option.Value));
        var values = (await settings.Api.GetValuesAsync()).Value;
        Assert.DoesNotContain(legacyStatusText, values.Keys);
        Assert.Equal(16, values["desktop.popup.titleFontSize"]);
        Assert.Equal(true, (await repository.LoadAsync()).Value[legacyStatusText]);
        Assert.Equal(6, definitions.Count(item => item.Category == SettingCategory.Normal));
        var timing = Assert.Single(definitions, item => item.Id == "desktop.edge.showTiming");
        Assert.Equal("Resident", timing.DefaultValue);
        Assert.Equal(["Resident", "Minimized", "Always"], timing.Options!.Select(option => option.Value));
        Assert.All(definitions, item => Assert.Equal(SettingsGroup.DesktopPresence, item.Group));
        Assert.All(definitions.Where(item => item.Id is "desktop.popup.fitContent" or "desktop.popup.padding" or
            "desktop.popup.cornerRadius" or "desktop.popup.rowGap" or "desktop.popup.rowPaddingY" or
            "desktop.popup.titleFontSize" or "desktop.popup.titleMaxLines" or "desktop.popup.showHints"),
            item => { Assert.Equal(SettingCategory.Advanced, item.Category); Assert.Equal(SettingPreviewMode.Reversible, item.PreviewMode); });
        var invalid = await settings.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["desktop.edge.visibleDepth"] = 64 });
        Assert.Contains("desktop.edge.visibleDepth", invalid.Value.Keys);
        invalid = await settings.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["desktop.popup.openColor"] = "red" });
        Assert.Contains("desktop.popup.openColor", invalid.Value.Keys);
        var saved = await settings.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["desktop.closeBehavior"] = "Exit" });
        Assert.Empty(saved.Value);
        Assert.Equal(DesktopCloseBehavior.Exit, module.Api.CloseBehavior);
        // 独立偏好通道只允许模块写入自己的隐藏本机设置。
        Assert.False((await settings.Api.SaveModulePreferenceAsync("desktop-presence", "desktop.popup.filter", "Open")).IsSuccess);
        Assert.False((await settings.Api.SaveModulePreferenceAsync("tasks", "desktop.popup.lastFilter", "Open")).IsSuccess);
        Assert.True((await settings.Api.SaveModulePreferenceAsync("desktop-presence", "desktop.popup.lastFilter", "Open")).IsSuccess);
        Assert.Equal("Open", (await settings.Api.GetValuesAsync()).Value["desktop.popup.lastFilter"]);
        var ui = new SettingsUiModule(new(settings.Api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await ui.ActivateAsync(); var page = Assert.Single(ui.Views).Factory.Create();
        await page.Controller.InitializeAsync();
        var model = page.View.DataContext;
        Assert.Equal(6, ((IList)model.GetType().GetProperty("VisibleRows")!.GetValue(model)!).Count);
        page.View.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative) });
        page.View.Measure(new Size(1200, 800)); page.View.Arrange(new Rect(0, 0, 1200, 800)); page.View.UpdateLayout();
        var activation = Descendants(page.View).OfType<ComboBox>().Single(combo =>
            combo.DataContext.GetType().GetProperty("Id")!.GetValue(combo.DataContext) as string == "desktop.edge.activation");
        Assert.Equal("SingleClick", activation.SelectedValue);
        model.GetType().GetProperty("ShowAdvanced")!.SetValue(model, true);
        Assert.Equal(definitions.Count(item => item.ShowInSettings),
            ((IList)model.GetType().GetProperty("VisibleRows")!.GetValue(model)!).Count);
        await page.Controller.DisposeAsync(); await ui.DisposeAsync();
        await module.StopAsync();
        Assert.Empty((await settings.Api.ListDefinitionsAsync()).Value);
    });

    [Fact]
    public Task Presence_localization_has_a_single_authoritative_key_set_for_every_definition() => Sta.Run(async () =>
    {
        await using var settings = new SettingsModule(new(new Repository())); await settings.ActivateAsync();
        await using var module = new DesktopPresenceModule(new(new Host(), new Factory(), settings.Api,
            new UiDispatcher(Dispatcher.CurrentDispatcher), new Diagnostics()));
        await module.ActivateAsync();
        var definitions = (await settings.Api.ListDefinitionsAsync()).Value;
        foreach (var language in new[] { MosaicLanguage.Chinese, MosaicLanguage.English })
        {
            var dictionary = PresenceLocalization.Dictionary(language);
            foreach (var definition in definitions)
            {
                Assert.True(dictionary.Contains(definition.DisplayNameKey!), $"{language}: {definition.DisplayNameKey}");
                Assert.True(dictionary.Contains(definition.DescriptionKey!), $"{language}: {definition.DescriptionKey}");
                foreach (var option in definition.Options ?? [])
                    Assert.True(dictionary.Contains(option.DisplayNameKey), $"{language}: {option.DisplayNameKey}");
            }
            // 每个定义恰好两条文案键：既不能缺，也不能像旧实现那样同时生成小写点分键和 PascalCase 键。
            Assert.Equal(definitions.Count * 2, dictionary.Keys.Cast<object>()
                .Count(key => key is string text && text.StartsWith("Settings.Setting.", StringComparison.Ordinal)));
        }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    [Fact]
    public Task Surface_creates_refreshes_and_releases_edge_popup_and_sessions() => Sta.Run(async () =>
    {
        await using var settings = new SettingsModule(new(new Repository())); await settings.ActivateAsync();
        var factory = new Factory();
        await using var module = new DesktopPresenceModule(new(new Host(), factory, settings.Api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Diagnostics()));
        await module.ActivateAsync();
        var surface = typeof(DesktopPresenceModule).GetField("_surface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(module)!;
        Window? WindowField(string name) => surface.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface) as Window;
        Assert.True((await module.Api.RequestParkAsync()).IsSuccess);
        var edge = Assert.IsType<Window>(WindowField("_edge"));
        Assert.True(edge.IsVisible); Assert.False(edge.ShowInTaskbar); Assert.True(edge.Topmost);
        Assert.True((await module.Api.RequestPopupAsync()).IsSuccess);
        var popup = Assert.IsType<Window>(WindowField("_popup"));
        Assert.True(popup.IsVisible); Assert.False(popup.ShowInTaskbar);
        Assert.Equal(380, popup.Width); Assert.InRange(popup.Height, 1, 560);
        Assert.Single(factory.Sessions);
        Assert.True(factory.Sessions[0].Options.ShowCompletionToggle); Assert.True(factory.Sessions[0].Options.ShowFilterSwitch);
        var preview = await settings.Api.BeginEditSessionAsync();
        await preview.Value.PreviewAsync(new Dictionary<string, object?> { ["desktop.popup.completionToggle"] = false, ["desktop.popup.filterSwitch"] = false }, 1);
        Assert.False(factory.Sessions.Last().Options.ShowCompletionToggle); Assert.False(factory.Sessions.Last().Options.ShowFilterSwitch);
        await preview.Value.DiscardAsync();
        Assert.True(factory.Sessions.Last().Options.ShowCompletionToggle); Assert.True(factory.Sessions.Last().Options.ShowFilterSwitch);
        // 预览切换筛选条会重建弹窗，之后的可见性以当前窗口为准。
        var current = Assert.IsType<Window>(WindowField("_popup"));
        Assert.True(current.IsVisible);
        // 最近筛选是独立的隐藏本机偏好：单独保存不应重建弹窗或关闭它。
        var sessionCount = factory.Sessions.Count;
        Assert.True((await settings.Api.SaveModulePreferenceAsync("desktop-presence", "desktop.popup.lastFilter", "Open")).IsSuccess);
        Assert.Equal(sessionCount, factory.Sessions.Count);
        Assert.True(current.IsVisible);
        Assert.Equal("Open", (await settings.Api.GetValuesAsync()).Value["desktop.popup.lastFilter"]);
        await preview.Value.DisposeAsync();
        var beforeRefresh = factory.Sessions.Count;
        Assert.True((await module.Api.RequestRefreshAsync()).IsSuccess);
        Assert.True(factory.Sessions[0].Closed); Assert.True(factory.Sessions[0].Disposed);
        Assert.Equal(beforeRefresh + 1, factory.Sessions.Count);
        Assert.False(current.IsVisible);
        await module.Api.RequestRestoreAsync();
        Assert.False(edge.IsVisible); Assert.Null(WindowField("_popup"));
        Assert.All(factory.Sessions, session => Assert.True(session.Disposed));
        await module.StopAsync();
        Assert.Null(WindowField("_edge"));
    });

    [Fact]
    public Task Peek_remembers_user_filter_across_reopen_restart_and_legacy_migration() => Sta.Run(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("mosaic-peek-filter-");
        try
        {
            var legacyPath = Path.Combine(directory.FullName, "Config", "task-peek-filter.txt");
            var repository = new Repository();
            var queries = new List<TaskFilter>();
            var api = new Api
            {
                Query = (query, _) =>
                {
                    queries.Add(query.Filter);
                    return Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([]));
                }
            };
            var dispatcher = new UiDispatcher(Dispatcher.CurrentDispatcher);
            var diagnostics = new Diagnostics();
            await using var tasks = new TasksUiModule(new(api, dispatcher, new Dialogs(), diagnostics));
            await tasks.ActivateAsync();

            async Task<(SettingsModule Settings, DesktopPresenceModule Presence)> StartAsync(Repository store)
            {
                var settings = new SettingsModule(new(store));
                await settings.ActivateAsync();
                var presence = new DesktopPresenceModule(new(new Host(), tasks.CompanionFactory, settings.Api,
                    dispatcher, diagnostics,
                    () => File.Exists(legacyPath) ? File.ReadAllText(legacyPath) : null));
                await presence.ActivateAsync();
                return (settings, presence);
            }

            async Task<FrameworkElement> OpenAsync(DesktopPresenceModule presence, TaskFilter expected, bool showSwitch = true)
            {
                var previousQueryCount = queries.Count;
                Assert.True((await presence.Api.RequestPopupAsync()).IsSuccess);
                await Drain();
                Assert.Equal(previousQueryCount + 1, queries.Count);
                Assert.Equal(expected, queries.Last());
                var surface = typeof(DesktopPresenceModule).GetField("_surface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presence)!;
                var popup = Assert.IsType<Window>(surface.GetType().GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface));
                var view = Assert.Single(Descendants(popup).OfType<Mosaic.Tasks.Wpf.Internal.TaskPeekView>());
                var filters = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("Filters"));
                Assert.Equal(showSwitch ? Visibility.Visible : Visibility.Collapsed, filters.Visibility);
                if (showSwitch) Assert.True(Filter(view, expected.ToString()).IsChecked);
                return view;
            }

            async Task ConfigureAsync(SettingsModule settings, Dictionary<string, object?> changes)
            {
                var result = await settings.Api.ProposeChangesAsync(changes);
                Assert.True(result.IsSuccess, result.Error?.Message);
                Assert.Empty(result.Value);
            }

            // 独立偏好保存是异步的（模块在设置操作内部回调 UI），轮询到仓储可读为止。
            async Task AssertSavedAsync(Repository store, string expected)
            {
                for (var attempt = 0; attempt < 200; attempt++)
                {
                    if (Equals((await store.LoadAsync()).Value.GetValueOrDefault("desktop.popup.lastFilter"), expected)) return;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
                Assert.Equal(expected, (await store.LoadAsync()).Value.GetValueOrDefault("desktop.popup.lastFilter"));
            }

            var (firstSettings, firstPresence) = await StartAsync(repository);
            await using (firstSettings)
            await using (firstPresence)
            {
                await ConfigureAsync(firstSettings, new() { ["desktop.popup.filter"] = "Completed", ["desktop.popup.autoHide"] = false });
                Assert.True((await firstPresence.Api.RequestParkAsync()).IsSuccess);
                var view = await OpenAsync(firstPresence, TaskFilter.Completed);
                Assert.False(File.Exists(legacyPath));
                Assert.Equal("Unset", (await firstSettings.Api.GetValuesAsync()).Value["desktop.popup.lastFilter"]);

                foreach (var choice in new[] { TaskFilter.All, TaskFilter.Open, TaskFilter.Completed })
                {
                    Filter(view, choice.ToString()).IsChecked = true;
                    await Drain();
                    Assert.Equal(choice, queries.Last());
                    await AssertSavedAsync(repository, choice.ToString());
                    Assert.True((await firstPresence.Api.RequestDismissAsync()).IsSuccess);
                    view = await OpenAsync(firstPresence, choice);
                }

                // 编辑会话活动期间切换筛选仍走独立保存；随后提交草稿不能把最近选择写回旧值。
                await using (var edit = (await firstSettings.Api.BeginEditSessionAsync()).Value)
                {
                    Assert.True((await edit.PreviewAsync(new Dictionary<string, object?>
                    {
                        ["desktop.popup.width"] = 400
                    }, 1)).IsSuccess);
                    Filter(view, "Open").IsChecked = true;
                    await Drain();
                    await AssertSavedAsync(repository, "Open");
                    Assert.True((await edit.CommitAsync(1)).IsSuccess);
                }
                await Drain();
                Assert.Equal("Open", (await repository.LoadAsync()).Value["desktop.popup.lastFilter"]);

                // 隐藏筛选切换时使用默认筛选，但仍保留最近选择供再次显示时恢复。
                Assert.True((await firstPresence.Api.RequestDismissAsync()).IsSuccess);
                await ConfigureAsync(firstSettings, new() { ["desktop.popup.filter"] = "All", ["desktop.popup.filterSwitch"] = false });
                await OpenAsync(firstPresence, TaskFilter.All, showSwitch: false);
                Assert.Equal("Open", (await repository.LoadAsync()).Value["desktop.popup.lastFilter"]);
                Assert.True((await firstPresence.Api.RequestDismissAsync()).IsSuccess);
                await ConfigureAsync(firstSettings, new() { ["desktop.popup.filterSwitch"] = true });
                await OpenAsync(firstPresence, TaskFilter.Open);
            }

            // 重启：同一仓储重新装配，最近选择不依赖私有文本路径。
            var (restartedSettings, restartedPresence) = await StartAsync(repository);
            await using (restartedSettings)
            await using (restartedPresence)
            {
                Assert.True((await restartedPresence.Api.RequestParkAsync()).IsSuccess);
                await OpenAsync(restartedPresence, TaskFilter.Open);
            }

            // 旧安装迁移：文件存在且设置里尚无值时导入最近选择。
            directory.Delete(recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            File.WriteAllText(legacyPath, "Open");
            var migratedRepository = new Repository();
            var (migratedSettings, migratedPresence) = await StartAsync(migratedRepository);
            await using (migratedSettings)
            await using (migratedPresence)
            {
                Assert.Equal("Open", (await migratedRepository.LoadAsync()).Value["desktop.popup.lastFilter"]);
                await ConfigureAsync(migratedSettings, new() { ["desktop.popup.filter"] = "Completed", ["desktop.popup.autoHide"] = false });
                Assert.True((await migratedPresence.Api.RequestParkAsync()).IsSuccess);
                await OpenAsync(migratedPresence, TaskFilter.Open);
                Assert.Equal("Open", File.ReadAllText(legacyPath));
            }

            // 重复迁移幂等：已有新值优先，旧文件既不覆盖也不删除。
            var keptRepository = new Repository();
            await keptRepository.SaveAsync(new Dictionary<string, object?> { ["desktop.popup.lastFilter"] = "All" });
            var (keptSettings, keptPresence) = await StartAsync(keptRepository);
            await using (keptSettings)
            await using (keptPresence)
            {
                Assert.Equal("All", (await keptRepository.LoadAsync()).Value["desktop.popup.lastFilter"]);
                Assert.Equal("Open", File.ReadAllText(legacyPath));
            }

            // 非法旧值忽略，保留文件。
            File.WriteAllText(legacyPath, "Invalid");
            var invalidRepository = new Repository();
            var (invalidSettings, invalidPresence) = await StartAsync(invalidRepository);
            await using (invalidSettings)
            await using (invalidPresence)
            {
                Assert.Equal(0, invalidRepository.Initializations);
                Assert.Equal("Unset", (await invalidSettings.Api.GetValuesAsync()).Value["desktop.popup.lastFilter"]);
                await ConfigureAsync(invalidSettings, new() { ["desktop.popup.filter"] = "Open", ["desktop.popup.autoHide"] = false });
                Assert.True((await invalidPresence.Api.RequestParkAsync()).IsSuccess);
                await OpenAsync(invalidPresence, TaskFilter.Open);
                Assert.Equal("Invalid", File.ReadAllText(legacyPath));
            }

            // 迁移失败不阻断启动，使用默认值；下一次启动可重试并成功。
            File.WriteAllText(legacyPath, "Completed");
            var failingRepository = new Repository { PendingInitializeFailures = 1 };
            var (failingSettings, failingPresence) = await StartAsync(failingRepository);
            await using (failingSettings)
            await using (failingPresence)
            {
                Assert.Equal("Unset", (await failingSettings.Api.GetValuesAsync()).Value["desktop.popup.lastFilter"]);
                await ConfigureAsync(failingSettings, new() { ["desktop.popup.filter"] = "All", ["desktop.popup.autoHide"] = false });
                Assert.True((await failingPresence.Api.RequestParkAsync()).IsSuccess);
                await OpenAsync(failingPresence, TaskFilter.All);
                Assert.Equal("Completed", File.ReadAllText(legacyPath));
            }
            var (retriedSettings, retriedPresence) = await StartAsync(failingRepository);
            await using (retriedSettings)
            await using (retriedPresence)
            {
                Assert.Equal("Completed", (await failingRepository.LoadAsync()).Value["desktop.popup.lastFilter"]);
                Assert.True((await retriedPresence.Api.RequestParkAsync()).IsSuccess);
                await OpenAsync(retriedPresence, TaskFilter.Completed);
            }
        }
        finally { if (Directory.Exists(directory.FullName)) directory.Delete(recursive: true); }
    });

    [Fact]
    public Task Popup_measures_generic_content_clamps_height_and_ignores_closed_session_notifications() => Sta.Run(async () =>
    {
        await using var settings = new SettingsModule(new(new Repository())); await settings.ActivateAsync();
        var factory = new Factory();
        await using var module = new DesktopPresenceModule(new(new Host(), factory, settings.Api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Diagnostics()));
        await module.ActivateAsync();
        var surface = typeof(DesktopPresenceModule).GetField("_surface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(module)!;
        Window Popup() => (Window)surface.GetType().GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        await module.Api.RequestParkAsync(); await module.Api.RequestPopupAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var shortHeight = Popup().Height;
        Assert.InRange(shortHeight, 80, 300);
        factory.Sessions[0].UpdateText(string.Concat(Enumerable.Repeat("一段足够长的通用内容，用于验证容器测量。\n", 100)));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.InRange(Popup().Height, shortHeight + 1, 560);
        await module.Api.RequestRefreshAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var replacement = Popup(); var replacementHeight = replacement.Height;
        factory.Sessions[0].UpdateText("旧会话");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Same(replacement, Popup()); Assert.Equal(replacementHeight, Popup().Height);
        await module.StopAsync();
    });

    [Fact]
    public Task Popup_more_menu_belongs_to_interaction_region_and_escape_closes_it_first() => Sta.Run(async () =>
    {
        await using var settings = new SettingsModule(new(new Repository())); await settings.ActivateAsync();
        await using var module = new DesktopPresenceModule(new(new Host(), new Factory(), settings.Api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Diagnostics()));
        await module.ActivateAsync(); await module.Api.RequestParkAsync(); await module.Api.RequestPopupAsync();
        var surface = typeof(DesktopPresenceModule).GetField("_surface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(module)!;
        var popup = (Window)surface.GetType().GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        var view = (FrameworkElement)popup.Content;
        var more = Descendants(view).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "PresenceMore");
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var menu = Assert.IsType<ContextMenu>(more.ContextMenu);
        Assert.True(menu.IsOpen);
        Assert.True((bool)surface.GetType().GetMethod("Inside", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, [true])!);
        Assert.True((bool)view.GetType().GetMethod("CloseMenuForEscape", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!);
        Assert.False(menu.IsOpen); Assert.True(popup.IsVisible);
        Assert.False((bool)view.GetType().GetMethod("CloseMenuForEscape", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!);
        await module.StopAsync();
    });

    [Fact]
    public Task Popup_chrome_renders_in_both_themes_and_compacts_at_small_heights() => Sta.Run(async () =>
    {
        var oldLanguage = MosaicLocalizationManager.Current;
        MosaicLocalizationManager.SetLanguage(MosaicLanguage.Chinese);
        var api = new Api { Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([
            Item("整理本周计划") with { AttachmentCount = 2 }, Item("完成模块边界检查", true), Item("继续下一步")])) };
        await using var tasks = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics())); await tasks.ActivateAsync();
        await using var settings = new SettingsModule(new(new Repository()));
        await using var presence = new DesktopPresenceModule(new(new Host(), tasks.CompanionFactory, settings.Api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Diagnostics()));
        await presence.ActivateAsync();
        var autoHideChange = await settings.Api.ProposeChangesAsync(new Dictionary<string, object?>
        {
            ["desktop.popup.autoHide"] = false
        });
        Assert.True(autoHideChange.IsSuccess);
        await presence.Api.RequestParkAsync(); await presence.Api.RequestPopupAsync();
        var surface = typeof(DesktopPresenceModule).GetField("_surface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presence)!;
        var popup = (Window)surface.GetType().GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        popup.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative) });
        foreach (var key in new[] { "Presence.Title", "Presence.Subtitle", "Presence.Dismiss", "Presence.Restore", "Presence.Exit", "Presence.ExitShort", "Presence.AutoHideHint" })
            popup.Resources[key] = MosaicLocalizationManager.TryFindResource(key)!;
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "Mosaic.sln"))) root = root.Parent;
        var directory = Path.Combine(root!.FullName, "artifacts/ui-preview"); Directory.CreateDirectory(directory);
        try
        {
            foreach (var (name, theme, width, height) in new[]
            {
                ("presence-popup-light", MosaicThemeVariant.Light, 380, 560),
                ("presence-popup-dark", MosaicThemeVariant.Dark, 380, 560),
                ("presence-popup-compact", MosaicThemeVariant.Light, 280, 300)
            })
            {
                MosaicThemeManager.Apply(popup.Resources, theme); popup.Width = width; popup.Height = height;
                popup.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var view = (FrameworkElement)popup.Content; view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
                var restore = Descendants(view).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "PresenceRestore");
                var point = restore.TranslatePoint(new Point(), view);
                Assert.InRange(point.X + restore.ActualWidth, 1, width); Assert.InRange(point.Y + restore.ActualHeight, 1, height);
            }
        }
        finally
        {
            MosaicThemeManager.Apply(popup.Resources, MosaicThemeVariant.Light);
            MosaicLocalizationManager.SetLanguage(oldLanguage); await presence.Api.StopAsync();
        }
    });

    [Fact]
    public Task Task_companion_preview_can_render_as_independent_read_only_view() => Sta.Run(async () =>
    {
        var api = new Api { Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([
            Item("整理本周计划"), Item("完成模块边界检查", true), Item("继续下一步")])) };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme"));
        await session.InitializeAsync();
        session.View.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative) });
        session.View.Measure(new Size(380, 420)); session.View.Arrange(new Rect(0, 0, 380, 420)); session.View.UpdateLayout();
        var image = new RenderTargetBitmap(380, 420, 96, 96, PixelFormats.Pbgra32); image.Render(session.View);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Mosaic.sln"))) root = root.Parent;
        var directory = Path.Combine(root!.FullName, "artifacts/ui-preview"); Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(directory, "presence-tasks-readonly.png")); encoder.Save(stream);
        Assert.DoesNotContain(Descendants(session.View), child => child is TextBox);
    });

    [Fact]
    public Task Companion_renders_status_and_limit_without_exposing_editors_and_requeries_each_open() => Sta.Run(async () =>
    {
        var queries = 0;
        var api = new Api { Query = (query, _) =>
        {
            queries++; Assert.Equal(TaskFilter.All, query.Filter);
            return Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([Item("Open title"), Item("Done title", true), Item("More")]));
        } };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        await using (var session = module.CompanionFactory.Create(new("All", 2, "Theme", "Theme")))
        {
            await session.InitializeAsync(); var texts = Texts(session.View);
            Assert.Contains("Open title", texts); Assert.Contains("Done title", texts); Assert.DoesNotContain("More", texts);
            Assert.Equal(2, Descendants(session.View).OfType<System.Windows.Shapes.Path>()
                .Count(path => path.Name == "CompletionMark" && path.Visibility == Visibility.Visible));
            foreach (var card in Descendants(session.View).OfType<Button>().Where(button => button.Name == "TaskCard"))
                Assert.DoesNotContain(Descendants(card).OfType<TextBlock>(), text => text.Text is "未完成" or "已完成");
            Assert.DoesNotContain(Descendants(session.View), element => element is CheckBox or TextBox);
            Assert.Contains(texts, text => text.Contains('3'));
        }
        await using (var second = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme"))) await second.InitializeAsync();
        Assert.Equal(2, queries);
        await module.StopAsync();
        Assert.Throws<InvalidOperationException>(() => module.CompanionFactory.Create(new("All", 12, "Theme", "Theme")));
    });

    [Fact]
    public Task Companion_ignores_old_and_closed_query_results_and_cancels_lifetime() => Sta.Run(async () =>
    {
        var first = new TaskCompletionSource<OperationResult<IReadOnlyList<TaskItemDto>>>();
        CancellationToken token = default; var calls = 0;
        var api = new Api { Query = (_, ct) => { if (++calls == 1) { token = ct; return first.Task; } return Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([Item("Newest")])); } };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("Open", 12, "Theme", "Theme"));
        var old = session.InitializeAsync(); await session.InitializeAsync();
        Assert.Contains("Newest", Texts(session.View));
        await session.CloseAsync(); Assert.True(token.IsCancellationRequested);
        first.SetResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([Item("Stale")])); await old;
        Assert.DoesNotContain("Stale", Texts(session.View));
    });

    [Fact]
    public Task Companion_visual_options_keep_full_accessible_status_and_bound_title_height() => Sta.Run(async () =>
    {
        var title = string.Concat(Enumerable.Repeat("非常长的中英文标题 Long task title ", 12));
        var api = new Api { Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([Item(title, true)])) };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme",
            RowGap: 4, RowPaddingY: 6, TitleFontSize: 20, TitleMaxLines: 1));
        var layoutChanges = 0; session.LayoutInvalidated += (_, _) => layoutChanges++;
        await session.InitializeAsync(); Texts(session.View);
        var titleText = Descendants(session.View).OfType<TextBlock>().Single(text => text.Text == title);
        Assert.Equal(20, titleText.FontSize); Assert.Equal(28, titleText.MaxHeight);
        Assert.Contains("已完成", System.Windows.Automation.AutomationProperties.GetName(titleText));
        Assert.Contains(title, System.Windows.Automation.AutomationProperties.GetName(titleText));
        var card = Assert.Single(Descendants(session.View).OfType<Button>(), button => button.Name == "TaskCard");
        Assert.DoesNotContain(Descendants(card).OfType<TextBlock>(), text => text.Text == "已完成");
        Assert.True(layoutChanges > 0);
        Assert.Single(Descendants(session.View).OfType<ScrollViewer>());
        Assert.DoesNotContain(Descendants(session.View), element => element is CheckBox or TextBox);
    });
}
