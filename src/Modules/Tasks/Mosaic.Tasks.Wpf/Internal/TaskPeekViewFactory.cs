using System.Windows;
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

internal sealed record TaskPeekRow(string Title, string Status, bool IsCompleted, CompanionViewOptions Options, Brush? CustomColor)
{
    public string AccessibleName => Title + "，" + Status;
    public string Geometry => IsCompleted ? "M 2,9 L 7,14 L 17,3" : "M 18,9 A 8,8 0 1 1 2,9 A 8,8 0 1 1 18,9";
    public bool HasCustomColor => CustomColor is not null;
    public Thickness Padding => new(0, Options.RowPaddingY, 0, Options.RowPaddingY);
    public Thickness Margin => new(0, 0, 0, Options.RowGap);
    public double FontSize => Options.TitleFontSize;
    public double LineHeight => Options.TitleFontSize * 1.4;
    public double MaximumTitleHeight => LineHeight * Options.TitleMaxLines;
    public Visibility StatusVisibility => Options.ShowStatusText ? Visibility.Visible : Visibility.Collapsed;
}

internal sealed class TaskPeekViewModel : ICompanionViewSession
{
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

    internal TaskPeekViewModel(TasksUiDependencies dependencies, CompanionViewOptions options)
    {
        _dependencies = dependencies; _options = options;
        _view.Retry.Click += Retry;
        MosaicLocalizationManager.LanguageChanged += LanguageChanged;
        MosaicThemeManager.ThemeChanged += LanguageChanged;
        UpdateView();
    }

    public FrameworkElement View => _view;
    public event EventHandler? LayoutInvalidated;
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) return Task.CompletedTask;
        var query = LoadAsync(++_generation, cancellationToken);
        _allQueries = Task.WhenAll(_allQueries, query);
        return query;
    }
    private async void Retry(object sender, RoutedEventArgs e) { if (!_loading) await InitializeAsync(); }
    private void LanguageChanged(object? sender, EventArgs e) { if (!_closed) UpdateView(); }

    private async Task LoadAsync(int generation, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _loading = true; _failed = false; UpdateView();
        try
        {
            var result = await _dependencies.Api.ListAsync(new TaskQuery(Enum.Parse<TaskFilter>(_options.Filter)), linked.Token);
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
    }

    private void UpdateView()
    {
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
            return new TaskPeekRow(title, status, item.IsCompleted, _options, brush);
        }).ToArray();
        _view.More.Text = TaskLocalization.Format("Tasks.Peek.More", "显示前 {0} 项，共 {1} 项。更多内容请打开主窗口。", _options.MaxItems, _items.Count);
        _view.More.Visibility = !state && _items.Count > _options.MaxItems ? Visibility.Visible : Visibility.Collapsed;
        LayoutInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public Task CloseAsync()
    {
        if (_closed) return Task.CompletedTask;
        _closed = true; ++_generation; _lifetime.Cancel();
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
        try { await _allQueries; } catch (Exception exception) { _dependencies.Diagnostics.Report(Guid.NewGuid().ToString("N"), exception); }
        finally { _lifetime.Dispose(); }
    }
}
