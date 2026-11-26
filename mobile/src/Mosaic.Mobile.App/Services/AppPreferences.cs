using Mosaic.Mobile.Presentation;

namespace Mosaic.Mobile.App.Services;

public sealed record AppPreferenceData(
    string? ServerUrl,
    ThemePreference Theme,
    AppLanguagePreference Language);

/// <summary>
/// Non-secret preferences only. The server address is not a credential; tokens
/// live exclusively in <c>ISecretStorage</c>.
/// </summary>
public sealed class AppPreferences
{
    private const string ServerKey = "server.url";
    private const string ThemeKey = "app.theme";
    private const string LanguageKey = "app.language";

    public AppPreferenceData Load()
    {
        var theme = Enum.TryParse<ThemePreference>(Preferences.Default.Get(ThemeKey, "FollowSystem"), out var parsedTheme)
            ? parsedTheme
            : ThemePreference.FollowSystem;
        var language = Enum.TryParse<AppLanguagePreference>(
            Preferences.Default.Get(LanguageKey, "FollowSystem"),
            out var parsedLanguage)
            ? parsedLanguage
            : AppLanguagePreference.FollowSystem;
        return new AppPreferenceData(
            Preferences.Default.Get(ServerKey, string.Empty),
            theme,
            language);
    }

    public void SaveServerUrl(string? url) =>
        Preferences.Default.Set(ServerKey, url ?? string.Empty);

    public void SaveTheme(ThemePreference theme) =>
        Preferences.Default.Set(ThemeKey, theme.ToString());

    public void SaveLanguage(AppLanguagePreference language) =>
        Preferences.Default.Set(LanguageKey, language.ToString());
}
