using System.Windows;
using Mosaic.Presentation.Contracts;

namespace Mosaic.Shell.Wpf;

internal enum ShellSidebarMode
{
    Expanded,
    Rail
}

public partial class ShellWindow : Window, IAsyncDisposable
{
    private const double CollapseBelowWidth = 1040;
    private const double ExpandAtWidth = 1060;

    public static readonly DependencyProperty IsSidebarCompactProperty = DependencyProperty.Register(
        nameof(IsSidebarCompact),
        typeof(bool),
        typeof(ShellWindow),
        new PropertyMetadata(false));

    private readonly ShellViewModel _model;
    private ShellSidebarMode _sidebarMode = ShellSidebarMode.Expanded;
    private bool _sidebarLayoutInitialized;

    public ShellWindow(ContributionRegistry contributions, IUserDialogService dialogs)
    {
        InitializeComponent(); _model = new(contributions, dialogs); DataContext = _model;
    }

    public bool IsSidebarCompact
    {
        get => (bool)GetValue(IsSidebarCompactProperty);
        private set => SetValue(IsSidebarCompactProperty, value);
    }

    internal ShellSidebarMode SidebarMode => _sidebarMode;

    public Task InitializeAsync() => _model.InitializeAsync();
    public Task<bool> CanCloseAsync() => _model.CanCloseAsync();
    public async ValueTask DisposeAsync() => await _model.DisposeAsync();

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateSidebarLayout(ActualWidth);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSidebarLayout(e.NewSize.Width);

    private void UpdateSidebarLayout(double width)
    {
        if (width <= 0) return;

        var next = _sidebarMode;
        if (_sidebarMode == ShellSidebarMode.Expanded && width < CollapseBelowWidth)
            next = ShellSidebarMode.Rail;
        else if (_sidebarMode == ShellSidebarMode.Rail && width >= ExpandAtWidth)
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
        SidebarColumn.Width = new GridLength(compact ? 64 : 190);
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
        WorkspaceStatusCard.Width = compact ? 44 : double.NaN;
        WorkspaceStatusCard.Height = compact ? 44 : double.NaN;
        WorkspaceStatusCard.Padding = compact ? new Thickness(0) : new Thickness(12, 10, 12, 10);
        TopBar.Padding = compact ? new Thickness(20, 0, 20, 0) : new Thickness(32, 0, 32, 0);
    }
}
