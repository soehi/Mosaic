using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using Mosaic.FloatingWindows.Contracts;
using System.Windows.Media;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskPeekViewFactory(TasksUiDependencies dependencies, Func<bool> active) : ICompanionViewFactory
{
    public ICompanionViewSession Create(CompanionViewOptions options)
    {
        if (!active()) throw new InvalidOperationException("Task summary capability is inactive.");
        if (options.MaxItems is < 1 or > 100 || !Enum.TryParse<TaskFilter>(options.Filter, out _) ||
            options.RowGap is < 0 or > 12 || options.RowPaddingY is < 6 or > 20 ||
            options.TitleFontSize is < 12 or > 20 || options.TitleMaxLines is < 1 or > 3)
            throw new ArgumentException("Invalid task summary options.", nameof(options));
        return new TaskPeekViewModel(dependencies, options);
    }
}

internal sealed record TaskPeekRow(Guid Id, long Revision, string Title, string Status, bool IsCompleted, CompanionViewOptions Options, Brush? CustomColor) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _busy;
    public bool IsBusy { get => _busy; set { _busy = value; PropertyChanged?.Invoke(this, new(nameof(CanComplete))); PropertyChanged?.Invoke(this, new(nameof(CompletionName))); PropertyChanged?.Invoke(this, new(nameof(BusyVisibility))); PropertyChanged?.Invoke(this, new(nameof(GlyphVisibility))); } }
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => IsBusy ? Visibility.Collapsed : Visibility.Visible;
    public bool CanComplete => !IsBusy;
    public string CompletionName => Title + " · " + TaskLocalization.Localize(IsBusy ? "Tasks.Peek.Processing" : IsCompleted ? "Tasks.Peek.Reopen" : "Tasks.Peek.Complete", IsBusy ? "处理中…" : IsCompleted ? "恢复未完成" : "标记完成");
    public Visibility CompletionVisibility => Options.ShowCompletionToggle ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StaticStatusVisibility => Options.ShowCompletionToggle ? Visibility.Collapsed : Visibility.Visible;
    public string AccessibleName => Title + "，" + Status + "，" + TaskLocalization.Localize("Tasks.Peek.ViewDetails", "查看任务详情");
    public string Geometry => IsCompleted ? "M 2,9 L 7,14 L 17,3" : "M 18,9 A 8,8 0 1 1 2,9 A 8,8 0 1 1 18,9";
    public bool HasCustomColor => CustomColor is not null;
    public Thickness Padding => new(8, Options.RowPaddingY, 8, Options.RowPaddingY);
    public Thickness Margin => new(0, 0, 0, Math.Max(4, Options.RowGap));
    public double FontSize => Options.TitleFontSize;
    public double LineHeight => Options.TitleFontSize * 1.4;
    public double MaximumTitleHeight => LineHeight * Options.TitleMaxLines;
    public Visibility StatusVisibility => Options.ShowStatusText ? Visibility.Visible : Visibility.Collapsed;
}

internal sealed class TaskPeekViewModel : ICompanionViewSession, ICompanionNavigation, ICompanionInteraction
{
    private bool _dragActive;
    private readonly HashSet<Guid> _pending = [];
    public bool IsInteracting => _dragActive || _pending.Count > 0;
    private Guid? _focusId;
    private string? _focusName;
    private TaskFilter _selectedFilter;
    private CancellationTokenSource? _listCancellation;
    private CancellationTokenSource? _feedbackCancellation;
    public event EventHandler? InteractionChanged;
    public bool CancelInteraction() => _view.CancelDrag();
    private readonly IDisposable? _changes;
    private async Task DetachAsync(TaskPeekRow row, FloatingPlacementHint? hint, CancellationToken cancellationToken)
    {
        if (_closed || _dependencies.Detach is not { } source) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        try
        {
            _view.DetachFeedback.Visibility = Visibility.Collapsed;
            var result = await source.RequestDetachAsync(new("tasks-ui", row.Id.ToString("D")), hint, linked.Token);
            if (!_closed && !linked.IsCancellationRequested && !result.IsSuccess) { _view.DetachFeedback.Text = result.Error!.Message; _view.DetachFeedback.Visibility = Visibility.Visible; LayoutInvalidated?.Invoke(this, EventArgs.Empty); }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex) { _dependencies.Diagnostics.Report(Guid.NewGuid().ToString("N"), ex); if (!_closed) { _view.DetachFeedback.Text = TaskLocalization.Localize("Tasks.Floating.Error", "无法打开小窗，请重试"); _view.DetachFeedback.Visibility = Visibility.Visible; } }
    }
    private readonly TasksUiDependencies _dependencies;
    private readonly CompanionViewOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskPeekView _view = new();
    private IReadOnlyList<TaskItemDto> _items = [];
    private Task _allQueries = Task.CompletedTask;
    private bool _disposed;
    private bool _closed;
    private int _generation;
    private bool _failed;
    private bool _loading = true;
    private Guid? _selectedId;
    private int _detailGeneration;
    private CancellationTokenSource? _detailCancellation;
    private TaskItemDto? _detail;
    private bool _detailLoading;
    private bool _detailFailed;
    private bool _detailMissing;
    private readonly TaskPeekDetailsView _detailsView;
    private IReadOnlyList<TaskAttachmentDto> _detailAttachments = [];
    private double _listOffset;
    private async void OpenDetails(TaskPeekRow row)
    {
        if (_closed) return;
        if (_selectedId is null) _listOffset = _view.ListScroll.VerticalOffset;
        _selectedId = row.Id;
        _detail = null;
        _view.ListPage.Visibility = Visibility.Collapsed;
        _view.DetailHost.Content = _detailsView;
        _view.DetailHost.Visibility = Visibility.Visible;
        _detailsView.Back = () => TryGoBack();
        _detailsView.Retry = () => StartDetailQuery(row.Id);
        var query = StartDetailQuery(row.Id);
        _detailsView.BackButton.Focus();
        await query;
    }
    private Task StartDetailQuery(Guid id)
    {
        _detailCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _detailCancellation = cancellation;
        var query = LoadDetailAsync(id, ++_detailGeneration, cancellation);
        _allQueries = Task.WhenAll(_allQueries, query);
        return query;
    }
    private async Task LoadDetailAsync(Guid id, int generation, CancellationTokenSource cancellation)
    {
        _detailLoading = true; _detailFailed = false; _detailMissing = false; RenderDetails();
        try
        {
            var result = await _dependencies.Api.GetAsync(id, cancellation.Token);
            await _dependencies.Dispatcher.InvokeAsync(() =>
            {
                if (_closed || cancellation.IsCancellationRequested || generation != _detailGeneration || _selectedId != id) return;
                _detailLoading = false; _detailFailed = !result.IsSuccess;
                _detailMissing = result.Error?.Code == Mosaic.Contracts.ErrorCodes.NotFound;
                _detail = result.IsSuccess ? result.Value : null;
                if (_detail is null) { _detailAttachments = []; _detailsView.Attachments?.Update(_detailAttachments); }
                RenderDetails();
                if (result.IsSuccess) TrackDetailAttachments(id, cancellation.Token);
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _dependencies.Diagnostics.Report(Guid.NewGuid().ToString("N"), exception);
            await _dependencies.Dispatcher.InvokeAsync(() =>
            {
                if (_closed || generation != _detailGeneration || _selectedId != id) return;
                _detailLoading = false; _detailFailed = true; RenderDetails();
            });
        }
        finally { if (ReferenceEquals(_detailCancellation, cancellation)) _detailCancellation = null; cancellation.Dispose(); }
    }
    private void TrackDetailAttachments(Guid id, CancellationToken token) =>
        _allQueries = Task.WhenAll(_allQueries, LoadDetailAttachmentsAsync(id, token));

    private async Task LoadDetailAttachmentsAsync(Guid id, CancellationToken token)
    {
        if (_dependencies.Attachments is null) return;
        try
        {
            var result = await _dependencies.Attachments.ListAsync(id, token);
            if (!result.IsSuccess) return;
            await _dependencies.Dispatcher.InvokeAsync(() =>
            {
                if (_closed || _detail?.Id != id) return;
                _detailAttachments = result.Value;
                _detailsView.Attachments?.Update(_detailAttachments);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { _dependencies.Diagnostics.Report(Guid.NewGuid().ToString("N"), exception); }
    }

    private void RenderDetails()
    {
        if (_selectedId is null || _closed) return;
        _detailsView.Render(_detail, _detailLoading, _detailFailed, _detailMissing);
        LayoutInvalidated?.Invoke(this, EventArgs.Empty);
    }
    public bool TryGoBack()
    {
        if (_selectedId is not { } id || _closed) return false;
        ++_detailGeneration; _detailCancellation?.Cancel(); _selectedId = null;
        _view.DetailHost.Visibility = Visibility.Collapsed; _view.ListPage.Visibility = Visibility.Visible;
        _view.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closed || _selectedId is not null) return;
            _view.ListScroll.ScrollToVerticalOffset(_listOffset);
            var container = _view.Rows.ItemContainerGenerator.ContainerFromItem(_view.Rows.Items.Cast<TaskPeekRow>().FirstOrDefault(row => row.Id == id));
            if (container is System.Windows.DependencyObject node) FindButton(node)?.Focus();
        }));
        LayoutInvalidated?.Invoke(this, EventArgs.Empty); return true;
    }
    private static System.Windows.Controls.Button? FindButton(DependencyObject node)
    {
        if (node is System.Windows.Controls.Button { Name: "TaskCard" } button) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            if (FindButton(VisualTreeHelper.GetChild(node, index)) is { } found) return found;
        return null;
    }

    internal TaskPeekViewModel(TasksUiDependencies dependencies, CompanionViewOptions options)
    {
        _dependencies = dependencies; _options = options;
        _detailsView = new(dependencies.Attachments);
        _selectedFilter = Enum.Parse<TaskFilter>(options.Filter);
        _view.CompleteRequested = CompleteAsync;
        _view.FilterRequested = SelectFilterAsync;
        _view.DismissFeedback.Click += (_, _) => { _feedbackCancellation?.Cancel(); _view.OperationFeedback.Visibility = Visibility.Collapsed; LayoutInvalidated?.Invoke(this, EventArgs.Empty); };
        _view.Retry.Click += Retry;
        _view.OpenRequested += OpenDetails;
        _view.CanDetach = () => !_closed && dependencies.Detach?.IsAvailable == true;
        _view.DetachRequested = DetachAsync;
        _view.PreviewRequested = (row, hint) => dependencies.Detach?.BeginPlacementPreview(new("tasks-ui", row.Id.ToString("D")), hint);
        _view.PreviewMoved = hint => dependencies.Detach?.UpdatePlacementPreview(hint);
        _view.PreviewEnded = () => dependencies.Detach?.EndPlacementPreview();
        _view.DragChanged += active => { _dragActive = active; InteractionChanged?.Invoke(this, EventArgs.Empty); };
        _changes = dependencies.Changes?.Subscribe(change => _ = dependencies.Dispatcher.InvokeAsync(() =>
        {
            if (_closed) return;
            _ = InitializeAsync();
            if (_selectedId == change.TaskId) _ = StartDetailQuery(change.TaskId);
        }));
        MosaicLocalizationManager.LanguageChanged += LanguageChanged;
        MosaicThemeManager.ThemeChanged += LanguageChanged;
        UpdateView();
    }

    public FrameworkElement View => _view;
    public event EventHandler? LayoutInvalidated;
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) return Task.CompletedTask;
        _listCancellation?.Cancel();
        _listCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var query = LoadAsync(++_generation, _listCancellation);
        _allQueries = Task.WhenAll(_allQueries, query);
        return query;
    }
    private async void Retry(object sender, RoutedEventArgs e) { if (!_loading) await InitializeAsync(); }
    private void LanguageChanged(object? sender, EventArgs e) { if (!_closed) { UpdateView(); RenderDetails(); } }

    private async Task LoadAsync(int generation, CancellationTokenSource linked)
    {
        using var owned = linked;
        _loading = true; _failed = false; UpdateView();
        try
        {
            var result = await _dependencies.Api.ListAsync(new TaskQuery(_selectedFilter), linked.Token);
            await _dependencies.Dispatcher.InvokeAsync(() =>
            {
                if (_closed || generation != _generation || linked.IsCancellationRequested) return;
                _loading = false; _failed = !result.IsSuccess;
                _items = result.IsSuccess ? result.Value : [];
                UpdateView();
            });
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _dependencies.Diagnostics.Report(Guid.NewGuid().ToString("N"), exception);
            if (!_closed && generation == _generation)
                await _dependencies.Dispatcher.InvokeAsync(() => { if (_closed) return; _loading = false; _failed = true; UpdateView(); });
        }
        finally { if (ReferenceEquals(_listCancellation, linked)) _listCancellation = null; }
    }

    private void UpdateView()
    {
        var focused = Keyboard.FocusedElement as FrameworkElement;
        if (focused?.DataContext is TaskPeekRow focusedRow) { _focusId = focusedRow.Id; _focusName = focused.Name; }
        var focusedId = _focusId; var focusedName = _focusName;
        _view.Filters.Visibility = _options.ShowFilterSwitch ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in _view.FilterOptions.Children.OfType<System.Windows.Controls.RadioButton>())
        {
            var filter = Enum.Parse<TaskFilter>((string)button.Tag);
            button.Content = TaskLocalization.Localize("Tasks.Peek.Filter." + filter, filter.ToString());
            AutomationProperties.SetName(button, (string)button.Content);
            button.IsChecked = filter == _selectedFilter;
        }
        _view.Summary.Text = _loading || _failed ? TaskLocalization.Localize("Tasks.Peek.Snapshot", "当前任务快照")
            : TaskLocalization.Format("Tasks.Peek.Count", "共 {0} 项 · {1} 项未完成", _items.Count, _items.Count(item => !item.IsCompleted));
        _view.Feedback.Text = _loading ? TaskLocalization.Localize("Tasks.Loading", "正在读取任务…")
            : _failed ? TaskLocalization.Localize("Tasks.LoadError", "任务列表加载失败")
            : _items.Count == 0 ? TaskLocalization.Localize("Tasks.Peek.Empty", "没有符合筛选的任务") : string.Empty;
        var state = _loading || _failed || _items.Count == 0;
        _view.State.Visibility = state ? Visibility.Visible : Visibility.Collapsed;
        _view.ListScroll.Visibility = state ? Visibility.Collapsed : Visibility.Visible;
        _view.Retry.Content = TaskLocalization.Localize("Tasks.Retry", "重试");
        _view.Retry.Visibility = _failed ? Visibility.Visible : Visibility.Collapsed;
        _view.Retry.IsEnabled = !_loading;
        _view.Rows.ItemsSource = _loading || _failed ? Array.Empty<TaskPeekRow>() : _items.Take(_options.MaxItems).Select(item =>
        {
            var status = TaskLocalization.Localize(item.IsCompleted ? "Tasks.Peek.Completed" : "Tasks.Peek.Open", item.IsCompleted ? "已完成" : "未完成");
            var title = string.IsNullOrWhiteSpace(item.Title) ? TaskLocalization.Localize("Tasks.Peek.Untitled", "未命名任务") : item.Title;
            var color = item.IsCompleted ? _options.CompletedColor : _options.OpenColor;
            Brush? brush = color != "Theme" && !SystemParameters.HighContrast && MosaicThemeManager.Current != MosaicThemeVariant.HighContrast &&
                color.Length == 7 && color[0] == '#' && color[1..].All(Uri.IsHexDigit)
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) : null;
            return new TaskPeekRow(item.Id, item.Revision, title, status, item.IsCompleted, _options, brush) { IsBusy = _pending.Contains(item.Id) };
        }).ToArray();
        _view.More.Text = TaskLocalization.Format("Tasks.Peek.More", "显示前 {0} 项，共 {1} 项。更多内容请打开主窗口。", _options.MaxItems, _items.Count);
        _view.More.Visibility = !state && _items.Count > _options.MaxItems ? Visibility.Visible : Visibility.Collapsed;
        if (!_loading && focusedId is { } id)
            _view.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed || _selectedId is not null) return;
                _view.UpdateLayout();
                var row = _view.Rows.Items.Cast<TaskPeekRow>().FirstOrDefault(item => item.Id == id);
                var node = row is null ? null : _view.Rows.ItemContainerGenerator.ContainerFromItem(row);
                var target = node is null ? null : Buttons(node).FirstOrDefault(button => button.Name == focusedName && button.IsEnabled && button.IsVisible);
                if (target is not null) target.Focus();
                else if (_options.ShowFilterSwitch) _view.FilterOptions.Children.OfType<RadioButton>().FirstOrDefault(button => button.IsChecked == true)?.Focus();
                else Buttons(_view.Rows).FirstOrDefault(button => button.IsEnabled && button.IsVisible)?.Focus();
                _focusId = null; _focusName = null;
            }));
        LayoutInvalidated?.Invoke(this, EventArgs.Empty);
    }
    private static IEnumerable<Button> Buttons(DependencyObject node)
    {
        if (node is Button button) yield return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Buttons(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
    private Task SelectFilterAsync(TaskFilter filter)
    {
        if (_closed || filter == _selectedFilter) return Task.CompletedTask;
        _view.CancelDrag(); _selectedFilter = filter;
        return InitializeAsync();
    }
    private Task CompleteAsync(TaskPeekRow row)
    {
        if (_closed || !_options.ShowCompletionToggle || !_pending.Add(row.Id)) return Task.CompletedTask;
        row.IsBusy = true; InteractionChanged?.Invoke(this, EventArgs.Empty);
        var operation = SubmitCompletionAsync(row);
        _allQueries = Task.WhenAll(_allQueries, operation);
        return operation;
    }
    private async Task SubmitCompletionAsync(TaskPeekRow row)
    {
        try
        {
            _view.OperationFeedback.Visibility = Visibility.Collapsed;
            var result = await _dependencies.Api.SetCompletedAsync(new(row.Id, !row.IsCompleted, row.Revision), _lifetime.Token);
            if (_closed) return;
            if (!result.IsSuccess) ShowOperationError(TaskErrorLocalizer.Localize(result.Error!));
            // Read the authoritative snapshot, including after a revision conflict.
            await InitializeAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _dependencies.Diagnostics.Report(Guid.NewGuid().ToString("N"), exception);
            if (!_closed) ShowOperationError(TaskLocalization.Localize("Tasks.Peek.CompletionFailed", "无法更改任务状态，请重试。"));
        }
        finally
        {
            _pending.Remove(row.Id); row.IsBusy = false;
            foreach (var current in _view.Rows.Items.OfType<TaskPeekRow>().Where(item => item.Id == row.Id)) current.IsBusy = false;
            InteractionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private void ShowOperationError(string text)
    {
        _feedbackCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _feedbackCancellation = cancellation;
        _view.OperationMessage.Text = text;
        _view.DismissFeedback.Content = TaskLocalization.Localize("Tasks.Peek.DismissFeedback", "关闭提示");
        _view.OperationFeedback.Visibility = Visibility.Visible;
        LayoutInvalidated?.Invoke(this, EventArgs.Empty);
        _allQueries = Task.WhenAll(_allQueries, HideFeedbackAsync(cancellation));
    }
    private async Task HideFeedbackAsync(CancellationTokenSource cancellation)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(6), cancellation.Token); if (!_closed) { _view.OperationFeedback.Visibility = Visibility.Collapsed; LayoutInvalidated?.Invoke(this, EventArgs.Empty); } }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_feedbackCancellation, cancellation)) _feedbackCancellation = null; cancellation.Dispose(); }
    }

    public Task CloseAsync()
    {
        if (_closed) return Task.CompletedTask;
        _view.CancelDrag(); _changes?.Dispose();
        _closed = true; ++_generation; ++_detailGeneration; _detailCancellation?.Cancel();
        _view.CompleteRequested = null; _view.FilterRequested = null;
        _view.PreviewRequested = null; _view.PreviewMoved = null; _view.PreviewEnded = null;
        _view.OpenRequested -= OpenDetails; _lifetime.Cancel();
        MosaicLocalizationManager.LanguageChanged -= LanguageChanged;
        MosaicThemeManager.ThemeChanged -= LanguageChanged;
        _view.Retry.Click -= Retry;
        LayoutInvalidated = null;
        return Task.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CloseAsync();
        try { await _allQueries.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { _ = DisposeLifetimeAfterQueriesAsync(); return; }
        finally { if (_allQueries.IsCompleted) _lifetime.Dispose(); }
    }
    private async Task DisposeLifetimeAfterQueriesAsync()
    {
        try { await _allQueries; }
        catch (Exception exception) { _dependencies.Diagnostics.Report(Guid.NewGuid().ToString("N"), exception); }
        finally { _lifetime.Dispose(); }
    }
}
