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

internal sealed class TaskPeekRow(Guid id, CompanionViewOptions options) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private long _revision;
    private string _title = string.Empty;
    private string _status = string.Empty;
    private bool _completed;
    private long _attachmentCount;
    private Brush? _customColor;
    private bool _busy;
    private string? _completionName;
    private string? _accessibleName;
    private string? _attachmentLabel;
    public Guid Id => id;
    public CompanionViewOptions Options => options;
    public long Revision => _revision;
    public string Title => _title;
    public string Status => _status;
    public bool IsCompleted => _completed;
    public long AttachmentCount => _attachmentCount;
    public Brush? CustomColor => _customColor;
    public bool IsBusy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            Notify(nameof(IsBusy), nameof(CanComplete), nameof(BusyVisibility), nameof(GlyphVisibility));
            RefreshText();
        }
    }
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => IsBusy ? Visibility.Collapsed : Visibility.Visible;
    public bool CanComplete => !IsBusy;
    public string CompletionName => Title + " · " + TaskLocalization.Localize(IsBusy ? "Tasks.Peek.Processing" : IsCompleted ? "Tasks.Peek.Reopen" : "Tasks.Peek.Complete", IsBusy ? "处理中…" : IsCompleted ? "恢复未完成" : "标记完成");
    public Visibility CompletionVisibility => Options.ShowCompletionToggle ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StaticStatusVisibility => Options.ShowCompletionToggle ? Visibility.Collapsed : Visibility.Visible;
    public bool HasAttachments => AttachmentCount > 0;
    public string AttachmentLabel => TaskLocalization.AttachmentCountLabel(AttachmentCount);
    public string AccessibleName => Title + "，" + Status + "，" + (HasAttachments ? AttachmentLabel + "，" : string.Empty)
        + TaskLocalization.Localize("Tasks.Peek.ViewDetails", "查看任务详情");
    public string Geometry => IsCompleted ? "M 2,9 L 7,14 L 17,3" : "M 18,9 A 8,8 0 1 1 2,9 A 8,8 0 1 1 18,9";
    public bool HasCustomColor => CustomColor is not null;
    public Thickness Padding => new(8, Options.RowPaddingY, 8, Options.RowPaddingY);
    public Thickness Margin => new(0, 0, 0, Math.Max(4, Options.RowGap));
    public double FontSize => Options.TitleFontSize;
    public double LineHeight => Options.TitleFontSize * 1.4;
    public double MaximumTitleHeight => LineHeight * Options.TitleMaxLines;

    /// <summary>Applies the authoritative snapshot to this reusable row, notifying only the fields that changed.</summary>
    internal void Update(long revision, string title, string status, bool completed, long attachmentCount, Brush? customColor)
    {
        if (!string.Equals(_title, title, StringComparison.Ordinal)) { _title = title; Notify(nameof(Title)); }
        if (!string.Equals(_status, status, StringComparison.Ordinal)) { _status = status; Notify(nameof(Status)); }
        if (_attachmentCount != attachmentCount) { _attachmentCount = attachmentCount; Notify(nameof(AttachmentCount), nameof(HasAttachments)); }
        if (_completed != completed) { _completed = completed; Notify(nameof(IsCompleted), nameof(Geometry)); }
        if (!SameBrush(_customColor, customColor)) { _customColor = customColor; Notify(nameof(CustomColor), nameof(HasCustomColor)); }
        if (_revision != revision) { _revision = revision; Notify(nameof(Revision)); }
    }

    /// <summary>Re-evaluates the composed, localized strings after a language, theme or state change.</summary>
    internal void RefreshText()
    {
        NotifyIfChanged(ref _completionName, CompletionName, nameof(CompletionName));
        NotifyIfChanged(ref _accessibleName, AccessibleName, nameof(AccessibleName));
        NotifyIfChanged(ref _attachmentLabel, AttachmentLabel, nameof(AttachmentLabel));
    }

    private static bool SameBrush(Brush? left, Brush? right) =>
        left is null ? right is null
        : right is not null && ((left is SolidColorBrush current && right is SolidColorBrush candidate && current.Color == candidate.Color) || ReferenceEquals(left, right));

    private void Notify(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new(name));
    }

    private void NotifyIfChanged(ref string? cached, string current, string name)
    {
        if (string.Equals(cached, current, StringComparison.Ordinal)) return;
        cached = current;
        Notify(name);
    }
}

internal sealed class TaskPeekViewModel : ICompanionViewSession, ICompanionFilterSelection, ICompanionNavigation, ICompanionInteraction
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
    public event EventHandler? DismissRequested;
    public event EventHandler<string>? FilterChanged;
    public bool CancelInteraction() => _view.CancelDrag();
    private readonly IDisposable? _changes;
    private async Task<bool> DetachAsync(TaskPeekRow row, FloatingPlacementHint? hint, CancellationToken cancellationToken)
    {
        if (_closed || _dependencies.Detach is not { } source) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        try
        {
            _view.DetachFeedback.Visibility = Visibility.Collapsed;
            var result = await source.RequestDetachAsync(new("tasks-ui", row.Id.ToString("D")), hint, linked.Token);
            if (_closed || linked.IsCancellationRequested) return false;
            if (result.IsSuccess) return true;
            ShowDetachError(result.Error!.Message);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _dependencies.Diagnostics.Report(Guid.NewGuid().ToString("N"), exception);
            if (!_closed && !linked.IsCancellationRequested)
                ShowDetachError(TaskLocalization.Localize("Tasks.Floating.Error", "无法打开小窗，请重试"));
        }
        return false;
    }
    private void ShowDetachError(string message)
    {
        _view.DetachFeedback.Text = message;
        _view.DetachFeedback.Visibility = Visibility.Visible;
        LayoutInvalidated?.Invoke(this, EventArgs.Empty);
    }
    private readonly TasksUiDependencies _dependencies;
    private readonly CompanionViewOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskPeekView _view = new();
    private IReadOnlyList<TaskItemDto> _items = [];
    private readonly Dictionary<Guid, TaskPeekRow> _rowsById = [];
    private TaskPeekRow[] _rows = [];
    private Task _allQueries = Task.CompletedTask;
    private bool _disposed;
    private bool _closed;
    private int _generation;
    private bool _failed;
    private bool _loading = true;
    private int _totalCount;
    private int _openCount;
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
            if (_rowsById.GetValueOrDefault(id) is { } row && _view.Rows.ItemContainerGenerator.ContainerFromItem(row) is System.Windows.DependencyObject node)
                FindButton(node)?.Focus();
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
        _view.DismissFeedback.Click += DismissFeedback;
        _view.Retry.Click += Retry;
        _view.OpenRequested += OpenDetails;
        _view.CanDetach = () => !_closed && dependencies.Detach?.IsAvailable == true;
        _view.DetachRequested = DetachAsync;
        _view.Detached += DragDetached;
        _view.PreviewRequested = (row, hint) => dependencies.Detach?.BeginPlacementPreview(new("tasks-ui", row.Id.ToString("D")), hint);
        _view.PreviewMoved = hint => dependencies.Detach?.UpdatePlacementPreview(hint);
        _view.PreviewEnded = () => dependencies.Detach?.EndPlacementPreview();
        _view.DragChanged += DragChanged;
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

    private void DragDetached()
    {
        if (!_closed) DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DragChanged(bool active)
    {
        if (_dragActive == active) return;
        _dragActive = active;
        InteractionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DismissFeedback(object? sender, RoutedEventArgs e)
    {
        CancelFeedback();
        _view.OperationFeedback.Visibility = Visibility.Collapsed;
        LayoutInvalidated?.Invoke(this, EventArgs.Empty);
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
            // The popup renders MaxItems rows: fetch that page without notes and count the rest with
            // one aggregate query instead of materializing the whole filtered table.
            var result = await _dependencies.Api.ListAsync(new TaskQuery(_selectedFilter) { Limit = _options.MaxItems, IncludeNotes = false }, linked.Token);
            var counts = result.IsSuccess ? await _dependencies.Api.CountAsync(new TaskQuery(_selectedFilter), linked.Token) : null;
            await _dependencies.Dispatcher.InvokeAsync(() =>
            {
                if (_closed || generation != _generation || linked.IsCancellationRequested) return;
                _loading = false; _failed = !result.IsSuccess;
                _items = result.IsSuccess ? result.Value : [];
                _totalCount = counts is { IsSuccess: true } ? counts.Value.Total : _items.Count;
                _openCount = counts is { IsSuccess: true } ? counts.Value.Open : _items.Count(item => !item.IsCompleted);
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
        }
        _view.SelectFilter(_selectedFilter);
        _view.Summary.Text = _loading || _failed ? TaskLocalization.Localize("Tasks.Peek.Snapshot", "当前任务快照")
            : TaskLocalization.Format("Tasks.Peek.Count", "共 {0} 项 · {1} 项未完成", _totalCount, _openCount);
        _view.Feedback.Text = _loading ? TaskLocalization.Localize("Tasks.Loading", "正在读取任务…")
            : _failed ? TaskLocalization.Localize("Tasks.LoadError", "任务列表加载失败")
            : _items.Count == 0 ? TaskLocalization.Localize("Tasks.Peek.Empty", "没有符合筛选的任务") : string.Empty;
        var state = _loading || _failed || _items.Count == 0;
        _view.State.Visibility = state ? Visibility.Visible : Visibility.Collapsed;
        _view.ListScroll.Visibility = state ? Visibility.Collapsed : Visibility.Visible;
        _view.Retry.Content = TaskLocalization.Localize("Tasks.Retry", "重试");
        _view.Retry.Visibility = _failed ? Visibility.Visible : Visibility.Collapsed;
        _view.Retry.IsEnabled = !_loading;
        var rows = BuildRows();
        if (!SameRows(rows))
        {
            _rows = rows;
            _view.Rows.ItemsSource = _rows;
            _view.UpdateLayout();
        }
        foreach (var row in _rows) row.RefreshText();
        _view.More.Text = TaskLocalization.Format("Tasks.Peek.More", "显示前 {0} 项，共 {1} 项。更多内容请打开主窗口。", _options.MaxItems, _totalCount);
        _view.More.Visibility = !state && _totalCount > _options.MaxItems ? Visibility.Visible : Visibility.Collapsed;
        if (!_loading && focusedId is { } id)
        {
            _focusId = null; _focusName = null;
            if (!_closed && _selectedId is null) RestoreFocus(id, focusedName);
        }
        LayoutInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private TaskPeekRow[] BuildRows()
    {
        // A reload keeps the existing rows so containers and focus survive; the list is hidden while loading.
        if (_loading) return _rows;
        if (_failed) { _rowsById.Clear(); return []; }
        var rows = new List<TaskPeekRow>(Math.Min(_options.MaxItems, _items.Count));
        var seen = new HashSet<Guid>();
        foreach (var item in _items.Take(_options.MaxItems))
        {
            var status = TaskLocalization.Localize(item.IsCompleted ? "Tasks.Peek.Completed" : "Tasks.Peek.Open", item.IsCompleted ? "已完成" : "未完成");
            var title = string.IsNullOrWhiteSpace(item.Title) ? TaskLocalization.Localize("Tasks.Peek.Untitled", "未命名任务") : item.Title;
            var color = item.IsCompleted ? _options.CompletedColor : _options.OpenColor;
            Brush? brush = color != "Theme" && !SystemParameters.HighContrast && MosaicThemeManager.Current != MosaicThemeVariant.HighContrast &&
                color.Length == 7 && color[0] == '#' && color[1..].All(Uri.IsHexDigit)
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) : null;
            if (!_rowsById.TryGetValue(item.Id, out var row))
            {
                row = new TaskPeekRow(item.Id, _options);
                _rowsById[item.Id] = row;
            }
            row.Update(item.Revision, title, status, item.IsCompleted, item.AttachmentCount, brush);
            var busy = _pending.Contains(item.Id);
            if (row.IsBusy != busy) row.IsBusy = busy;
            rows.Add(row);
            seen.Add(item.Id);
        }
        foreach (var stale in _rowsById.Keys.Where(id => !seen.Contains(id)).ToArray()) _rowsById.Remove(stale);
        return [.. rows];
    }

    private bool SameRows(IReadOnlyList<TaskPeekRow> rows)
    {
        if (rows.Count != _rows.Length) return false;
        for (var index = 0; index < rows.Count; index++)
            if (!ReferenceEquals(rows[index], _rows[index])) return false;
        return true;
    }

    private void RestoreFocus(Guid id, string? name)
    {
        if (!_rowsById.TryGetValue(id, out var row) || _view.Rows.ItemContainerGenerator.ContainerFromItem(row) is not DependencyObject node)
        {
            FocusFallback();
            return;
        }
        var target = Buttons(node).FirstOrDefault(button => button.Name == name && button.IsEnabled && button.IsVisible)
            ?? Buttons(node).FirstOrDefault(button => button.Name == "TaskCard" && button.IsEnabled && button.IsVisible);
        if (target is not null) target.Focus(); else FocusFallback();
    }

    private void FocusFallback()
    {
        if (_options.ShowFilterSwitch && _view.FilterOptions.Children.OfType<RadioButton>().FirstOrDefault(button => button.IsChecked == true) is { } filter)
        {
            filter.Focus();
            return;
        }
        Buttons(_view.Rows).FirstOrDefault(button => button.IsEnabled && button.IsVisible)?.Focus();
    }
    private static IEnumerable<Button> Buttons(DependencyObject node)
    {
        if (node is Button button) yield return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Buttons(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
    private Task SelectFilterAsync(TaskFilter filter)
    {
        if (_closed || !_options.ShowFilterSwitch || filter == _selectedFilter) return Task.CompletedTask;
        _view.CancelDrag();
        _selectedFilter = filter;
        FilterChanged?.Invoke(this, filter.ToString());
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
            _pending.Remove(row.Id);
            row.IsBusy = false;
            InteractionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private void ShowOperationError(string text)
    {
        CancelFeedback();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _feedbackCancellation = cancellation;
        _view.OperationMessage.Text = text;
        _view.DismissFeedback.Content = TaskLocalization.Localize("Tasks.Peek.DismissFeedback", "关闭提示");
        _view.OperationFeedback.Visibility = Visibility.Visible;
        LayoutInvalidated?.Invoke(this, EventArgs.Empty);
        _allQueries = Task.WhenAll(_allQueries, HideFeedbackAsync(cancellation));
    }
    // The field only ever holds a live token source: HideFeedbackAsync disposes its own instance after
    // clearing the field, so cancelling through the field cannot touch a disposed source.
    private void CancelFeedback()
    {
        var cancellation = _feedbackCancellation;
        if (cancellation is null) return;
        _feedbackCancellation = null;
        cancellation.Cancel();
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
        _closed = true; ++_generation; ++_detailGeneration; _detailCancellation?.Cancel();
        _view.CancelDrag(); _changes?.Dispose(); CancelFeedback();
        _view.DismissFeedback.Click -= DismissFeedback; _view.DragChanged -= DragChanged;
        _view.CanDetach = null;
        _view.CompleteRequested = null; _view.FilterRequested = null;
        _view.DetachRequested = null; _view.Detached -= DragDetached;
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
