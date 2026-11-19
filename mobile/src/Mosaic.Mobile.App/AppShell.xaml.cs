using Mosaic.Mobile.App.Pages;
using Mosaic.Mobile.Presentation;

namespace Mosaic.Mobile.App;

public partial class AppShell : Shell
{
    private readonly LocalizationService _localization;

    public AppShell(
        LocalizationService localization,
        TasksPage tasksPage,
        AccountPage accountPage,
        SettingsPage settingsPage)
    {
        InitializeComponent();
        _localization = localization;
        BindingContext = this;
        TasksTab.Content = tasksPage;
        AccountTab.Content = accountPage;
        SettingsTab.Content = settingsPage;
        localization.LanguageChanged += (_, _) => NotifyTitlesChanged();
    }

    // Binding paths cannot contain dots (Strings[nav.tasks] is parsed as a
    // member chain), so tab titles go through plain properties.
    public string TasksTitle => _localization.T(StringKeys.Tasks);

    public string AccountTitle => _localization.T(StringKeys.Account);

    public string SettingsTitle => _localization.T(StringKeys.Settings);

    private void NotifyTitlesChanged()
    {
        OnPropertyChanged(nameof(TasksTitle));
        OnPropertyChanged(nameof(AccountTitle));
        OnPropertyChanged(nameof(SettingsTitle));
    }
}
