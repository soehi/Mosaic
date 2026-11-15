using System.Windows;
using System.Windows.Controls;
using Mosaic.Presentation.Contracts;

namespace Mosaic.Shell.Wpf;

internal enum ShellSidebarMode
{
    Expanded,
    Rail
}

public partial class ShellWindow : Window, IAsyncDisposable
{
    public static readonly DependencyProperty IsSidebarCompactProperty = DependencyProperty.Register(
        nameof(IsSidebarCompact),
        typeof(bool),
        typeof(ShellWindow),
        new PropertyMetadata(false));

    private readonly ShellViewModel _model;
    private ShellSidebarMode _sidebarMode = ShellSidebarMode.Expanded;
    private bool _sidebarLayoutInitialized;
    private double _collapseBelowWidth = 1040;
    private double _expandAtWidth = 1060;
    private double _fullWidth = 190;
    private double _railWidth = 64;
    private Thickness _pageMargin = new(32, 30, 28, 28);

    public ShellWindow(ContributionRegistry contributions, IUserDialogService dialogs, Mosaic.Settings.Contracts.ISettingsApi? settings = null)
    {
        InitializeComponent(); _model = new(contributions, dialogs, settings); DataContext = _model;
        ShellSettingsApplier.Attach(this);
        Closed += (_, _) => ShellSettingsApplier.Detach(this);
    }

    public bool IsSidebarCompact
    {
        get => (bool)GetValue(IsSidebarCompactProperty);
        private set => SetValue(IsSidebarCompactProperty, value);
    }

    internal ShellSidebarMode SidebarMode => _sidebarMode;

    public async Task InitializeAsync()
    {
        await _model.InitializeAsync();
        if (_model.InitialValues is { } values) ApplyWindowMetrics(values);
    }
    public Task<bool> CanCloseAsync() => _model.CanCloseAsync();
    public async ValueTask DisposeAsync() => await _model.DisposeAsync();

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateSidebarLayout(ActualWidth);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSidebarLayout(e.NewSize.Width);

    private void UpdateSidebarLayout(double width)
    {
        if (width <= 0) return;

        var next = _sidebarMode;
        if (_sidebarMode == ShellSidebarMode.Expanded && width < _collapseBelowWidth)
            next = ShellSidebarMode.Rail;
        else if (_sidebarMode == ShellSidebarMode.Rail && width >= _expandAtWidth)
            next = ShellSidebarMode.Expanded;

        if (_sidebarLayoutInitialized && next == _sidebarMode) return;
        _sidebarMode = next;
        _sidebarLayoutInitialized = true;
        ApplySidebarLayout();
    }

    private void ApplySidebarLayout()
    {
        var compact = _sidebarMode == ShellSidebarMode.Rail;
        IsSidebarCompact = compact;
        SidebarColumn.Width = new GridLength(compact ? _railWidth : _fullWidth);
        SidebarContent.Margin = compact ? new Thickness(10, 20, 10, 18) : new Thickness(20, 28, 16, 22);
        BrandPanel.Margin = compact ? new Thickness(0, 0, 0, 28) : new Thickness(2, 0, 0, 42);
        BrandLockup.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        BrandMark.Width = compact ? 40 : 30;
        BrandMark.Height = compact ? 40 : 30;
        BrandMark.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 9, 0);
        BrandText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BrandTagline.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        StatusDetail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceStatusDot.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        WorkspaceStatusDot.Width = compact ? 9 : 7;
        WorkspaceStatusDot.Height = compact ? 9 : 7;
        WorkspaceStatusCard.Width = double.NaN;
        WorkspaceStatusCard.Height = double.NaN;
        WorkspaceStatusCard.Padding = compact ? new Thickness(0) : new Thickness(12, 10, 12, 10);
        WorkspaceStatusCard.Background = compact
            ? System.Windows.Media.Brushes.Transparent
            : (System.Windows.Media.Brush?)TryFindResource(Mosaic.Presentation.Wpf.MosaicTheme.SurfaceMutedBrushKey) ?? System.Windows.Media.Brushes.Transparent;
        WorkspaceStatusCard.BorderBrush = compact
            ? System.Windows.Media.Brushes.Transparent
            : (System.Windows.Media.Brush?)TryFindResource(Mosaic.Presentation.Wpf.MosaicTheme.BorderBrushKey) ?? System.Windows.Media.Brushes.Transparent;
        WorkspaceStatusCard.BorderThickness = compact ? new Thickness(0) : new Thickness(1);
        TopBar.Padding = compact ? new Thickness(20, 0, 20, 0) : new Thickness(32, 0, 32, 0);
        PageContent.Margin = _pageMargin;
    }

    private void ApplyWindowMetrics(IReadOnlyDictionary<string, object?> values)
    {
        if (values.TryGetValue("startup.width", out var width) && width is int startupWidth) Width = startupWidth;
        if (values.TryGetValue("startup.height", out var height) && height is int startupHeight) Height = startupHeight;
        if (values.TryGetValue("shell.window.minWidth", out var minWidth) && minWidth is int windowMinWidth) MinWidth = windowMinWidth;
        if (values.TryGetValue("shell.window.minHeight", out var minHeight) && minHeight is int windowMinHeight) MinHeight = windowMinHeight;
    }

    internal void ApplyLayoutSettings(IReadOnlyDictionary<string, object?> values)
    {
        if (values.TryGetValue("shell.sidebar.collapseBelowWidth", out var collapse) && collapse is int collapseWidth)
            _collapseBelowWidth = collapseWidth;
        if (values.TryGetValue("shell.sidebar.expandAtWidth", out var expand) && expand is int expandWidth)
            _expandAtWidth = expandWidth;
        if (values.TryGetValue("shell.sidebar.fullWidth", out var full) && full is int fullWidth)
            _fullWidth = fullWidth;
        if (values.TryGetValue("shell.sidebar.railWidth", out var rail) && rail is int railWidth)
            _railWidth = railWidth;
        if (values.TryGetValue("shell.page.marginLeft", out var left) && left is int marginLeft &&
            values.TryGetValue("shell.page.marginTop", out var top) && top is int marginTop &&
            values.TryGetValue("shell.page.marginRight", out var right) && right is int marginRight &&
            values.TryGetValue("shell.page.marginBottom", out var bottom) && bottom is int marginBottom)
            _pageMargin = new Thickness(marginLeft, marginTop, marginRight, marginBottom);
        UpdateSidebarLayout(ActualWidth);
        ApplySidebarLayout();
    }
}
