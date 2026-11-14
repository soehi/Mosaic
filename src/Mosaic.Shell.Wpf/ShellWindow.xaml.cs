using System.Windows;
using Mosaic.Presentation.Contracts;

namespace Mosaic.Shell.Wpf;

public partial class ShellWindow : Window, IAsyncDisposable
{
    private readonly ShellViewModel _model;
    public ShellWindow(ContributionRegistry contributions, IUserDialogService dialogs)
    {
        InitializeComponent(); _model = new(contributions, dialogs); DataContext = _model;
    }
    public Task InitializeAsync() => _model.InitializeAsync();
    public Task<bool> CanCloseAsync() => _model.CanCloseAsync();
    public async ValueTask DisposeAsync() => await _model.DisposeAsync();
}
