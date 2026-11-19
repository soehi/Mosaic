using Mosaic.Mobile.App.Services;
using Mosaic.Mobile.Presentation;

namespace Mosaic.Mobile.App.Pages;

public partial class TasksPage : ContentPage
{
    private readonly TaskListViewModel _viewModel;
    private readonly StartupService _startup;

    public TasksPage(TaskListViewModel viewModel, StartupService startup)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _startup = startup;
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _startup.InitializeAsync();
            await _viewModel.LoadAsync();
        }
        catch (Exception)
        {
            // The sync status already conveys failures; never surface raw text.
        }
    }
}
