using System.Windows;
using System.Windows.Controls;
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
        if (options.MaxItems is < 1 or > 100 || !Enum.TryParse<TaskFilter>(options.Filter, out _))
            throw new ArgumentException("Invalid task summary options.", nameof(options));
        return new TaskPeekViewModel(dependencies, options);
    }
}

internal sealed class TaskPeekViewModel : ICompanionViewSession
{
    private readonly TasksUiDependencies _dependencies;
    private readonly CompanionViewOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _rows = new();
    private readonly TextBlock _summary = new() { FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Border _state = new() { Padding = new Thickness(16, 22, 16, 22), CornerRadius = new CornerRadius(10) };
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) };
    private readonly Button _retry = new() { Margin = new Thickness(16, 0, 16, 16), HorizontalAlignment = HorizontalAlignment.Left };
    private IReadOnlyList<TaskItemDto> _items = [];
    private Task? _query;
    private Task _allQueries = Task.CompletedTask;
    private bool _disposed;
    private bool _closed;
    private int _generation;
    private bool _failed;
    private bool _loading = true;

    internal TaskPeekViewModel(TasksUiDependencies dependencies, CompanionViewOptions options)
    {
        _dependencies = dependencies; _options = options;
        var panel = new StackPanel { Margin = new Thickness(14, 14, 14, 14) };
        var stateContent = new StackPanel();
        var stateIcon = new TextBlock { Text = "○", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        stateIcon.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.AccentTextBrushKey);
        _feedback.Margin = new Thickness(0);
        _feedback.TextAlignment = TextAlignment.Center;
        stateContent.Children.Add(stateIcon); stateContent.Children.Add(_feedback); stateContent.Children.Add(_retry);
        _state.Child = stateContent;
        _state.SetResourceReference(Border.BackgroundProperty, MosaicTheme.SurfaceMutedBrushKey);
        _summary.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.TextSecondaryBrushKey);
        panel.Children.Add(_summary); panel.Children.Add(_state); panel.Children.Add(_rows);
        View = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0) };
        View.SetResourceReference(Control.ForegroundProperty, MosaicTheme.TextPrimaryBrushKey);
        View.SetResourceReference(Control.BackgroundProperty, MosaicTheme.SurfaceBrushKey);
        System.Windows.Automation.AutomationProperties.SetAutomationId(View, "TaskPeekView");
        _retry.Click += Retry;
        MosaicLocalizationManager.LanguageChanged += LanguageChanged;
        MosaicThemeManager.ThemeChanged += LanguageChanged;
        UpdateView();
    }

    public FrameworkElement View { get; }
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) return Task.CompletedTask;
        _query = LoadAsync(++_generation, cancellationToken);
        _allQueries = Task.WhenAll(_allQueries, _query);
        return _query;
    }
    private async void Retry(object sender, RoutedEventArgs e) => await InitializeAsync();
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
        _summary.Text = _loading || _failed ? TaskLocalization.Localize("Tasks.Peek.Snapshot", "当前任务快照")
            : TaskLocalization.Format("Tasks.Peek.Count", "共 {0} 项 · {1} 项未完成", _items.Count, _items.Count(item => !item.IsCompleted));
        _feedback.Text = _loading ? TaskLocalization.Localize("Tasks.Loading", "正在读取任务…")
            : _failed ? TaskLocalization.Localize("Tasks.LoadError", "任务列表加载失败")
            : _items.Count == 0 ? TaskLocalization.Localize("Tasks.Peek.Empty", "没有符合筛选的任务")
            : string.Empty;
        _state.Visibility = _loading || _failed || _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _feedback.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.TextSecondaryBrushKey);
        _retry.Content = TaskLocalization.Localize("Tasks.Retry", "重试");
        _retry.Margin = new Thickness(0, 12, 0, 0);
        _retry.HorizontalAlignment = HorizontalAlignment.Center;
        _retry.SetResourceReference(FrameworkElement.StyleProperty, MosaicTheme.QuietButtonKey);
        _retry.Visibility = _failed ? Visibility.Visible : Visibility.Collapsed;
        _retry.IsEnabled = !_loading;
        _rows.Children.Clear();
        if (_loading || _failed) return;
        foreach (var item in _items.Take(_options.MaxItems))
        {
            var status = TaskLocalization.Localize(item.IsCompleted ? "Tasks.Peek.Completed" : "Tasks.Peek.Open", item.IsCompleted ? "已完成" : "未完成");
            var title = string.IsNullOrWhiteSpace(item.Title) ? TaskLocalization.Localize("Tasks.Peek.Untitled", "未命名任务") : item.Title;
            var titleText = new TextBlock { Text = title, FontSize = 14, FontWeight = item.IsCompleted ? FontWeights.Normal : FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 44, ToolTip = title };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, item.IsCompleted ? MosaicTheme.TextSecondaryBrushKey : MosaicTheme.TextPrimaryBrushKey);
            var statusText = new TextBlock { Text = status, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
            var mark = new TextBlock { Text = item.IsCompleted ? "✓" : "○", FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var color = item.IsCompleted ? _options.CompletedColor : _options.OpenColor;
            foreach (var indicator in new[] { mark, statusText })
            {
                if (color != "Theme" && !SystemParameters.HighContrast && MosaicThemeManager.Current != MosaicThemeVariant.HighContrast && color.Length == 7 && color[0] == '#' && color[1..].All(Uri.IsHexDigit))
                    indicator.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                else indicator.SetResourceReference(TextBlock.ForegroundProperty, item.IsCompleted ? MosaicTheme.TextSecondaryBrushKey : MosaicTheme.AccentTextBrushKey);
            }
            var icon = new Border { Child = mark, Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            icon.SetResourceReference(Border.BackgroundProperty, item.IsCompleted ? MosaicTheme.SurfaceMutedBrushKey : MosaicTheme.AccentSoftBrushKey);
            var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new());
            var content = new StackPanel(); content.Children.Add(titleText); content.Children.Add(statusText); Grid.SetColumn(content, 1);
            grid.Children.Add(icon); grid.Children.Add(content);
            var row = new Border { Child = grid, Padding = new Thickness(13, 14, 13, 14), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Focusable = true };
            row.SetResourceReference(Border.BackgroundProperty, MosaicTheme.SurfaceBrushKey);
            row.SetResourceReference(Border.BorderBrushProperty, MosaicTheme.BorderBrushKey);
            System.Windows.Automation.AutomationProperties.SetName(row, title + "，" + status);
            _rows.Children.Add(row);
        }
        if (_items.Count > _options.MaxItems)
        {
            var more = new TextBlock { Text = TaskLocalization.Format("Tasks.Peek.More", "显示前 {0} 项，共 {1} 项。更多内容请打开主窗口。", _options.MaxItems, _items.Count),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 0) };
            more.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.TextSecondaryBrushKey);
            _rows.Children.Add(more);
        }
    }

    public Task CloseAsync()
    {
        if (_closed) return Task.CompletedTask;
        _closed = true; ++_generation; _lifetime.Cancel();
        MosaicLocalizationManager.LanguageChanged -= LanguageChanged;
        MosaicThemeManager.ThemeChanged -= LanguageChanged;
        _retry.Click -= Retry;
        return Task.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CloseAsync();
        try { await _allQueries.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException)
        {
            _ = DisposeLifetimeAfterQueriesAsync();
            return;
        }
        finally { if (_allQueries.IsCompleted) _lifetime.Dispose(); }
    }
    private async Task DisposeLifetimeAfterQueriesAsync()
    {
        try { await _allQueries; } catch (Exception exception) { _dependencies.Diagnostics.Report(Guid.NewGuid().ToString("N"), exception); }
        finally { _lifetime.Dispose(); }
    }
}
