using Mosaic.Mobile.App.Pages;
using Mosaic.Mobile.Presentation;

namespace Mosaic.Mobile.App;

public partial class AppShell : Shell
{
    public AppShell(
        LocalizationService localization,
        TasksPage tasksPage,
        AccountPage accountPage,
        SettingsPage settingsPage)
    {
        InitializeComponent();
        BindingContext = localization;
        TasksTab.Content = tasksPage;
        AccountTab.Content = accountPage;
        SettingsTab.Content = settingsPage;
    }
}
