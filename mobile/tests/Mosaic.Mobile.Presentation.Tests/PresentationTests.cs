using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Presentation;
using Mosaic.Mobile.Sync;

namespace Mosaic.Mobile.Presentation.Tests;

public sealed class PresentationTests
{
    [Theory]
    [InlineData(ThemePreference.FollowSystem, true, true)]
    [InlineData(ThemePreference.FollowSystem, false, false)]
    [InlineData(ThemePreference.Light, true, false)]
    [InlineData(ThemePreference.Dark, false, true)]
    public void Theme_resolution(ThemePreference preference, bool systemDark, bool expected)
    {
        Assert.Equal(expected, AppThemeService.ResolveIsDark(preference, systemDark));
    }

    [Fact]
    public void Language_follows_system_chinese_and_english()
    {
        var localizer = new LocalizationService();

        localizer.Apply(AppLanguagePreference.FollowSystem, "zh-Hans");
        Assert.Equal(AppLanguage.Chinese, localizer.Current);
        Assert.Equal("登录", localizer.T(StringKeys.SignIn));

        localizer.Apply(AppLanguagePreference.FollowSystem, "en-US");
        Assert.Equal(AppLanguage.English, localizer.Current);
        Assert.Equal("Sign in", localizer.T(StringKeys.SignIn));

        localizer.Apply(AppLanguagePreference.Chinese, "en-US");
        Assert.Equal(AppLanguage.Chinese, localizer.Current);
    }

    [Fact]
    public void Language_change_raises_once()
    {
        var localizer = new LocalizationService();
        var raised = 0;
        localizer.LanguageChanged += (_, _) => raised++;

        localizer.Apply(AppLanguagePreference.English, "en-US");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Format_uses_the_current_language()
    {
        var localizer = new LocalizationService();
        localizer.Apply(AppLanguagePreference.Chinese, "zh-Hans");
        Assert.Equal("30 秒后可重发", localizer.Format(StringKeys.ResendIn, 30));

        localizer.Apply(AppLanguagePreference.English, "en-US");
        Assert.Equal("Resend in 30s", localizer.Format(StringKeys.ResendIn, 30));
    }

    [Fact]
    public void Unknown_key_returns_the_key()
    {
        Assert.Equal("missing.key", new LocalizationService().T("missing.key"));
    }

    [Theory]
    [InlineData(AuthErrorKind.Network, StringKeys.NetworkError)]
    [InlineData(AuthErrorKind.StorageUnavailable, StringKeys.StorageUnavailable)]
    [InlineData(AuthErrorKind.SessionRevoked, StringKeys.SessionExpired)]
    [InlineData(AuthErrorKind.InvalidCredentials, StringKeys.InvalidCredentials)]
    public void Auth_errors_map_to_localized_text(AuthErrorKind kind, string expectedKey)
    {
        var localizer = new LocalizationService();
        localizer.Apply(AppLanguagePreference.English, "en-US");

        var text = AuthErrorText.Describe(localizer, new AuthError(kind));

        Assert.Equal(localizer.T(expectedKey), text);
    }

    [Fact]
    public void Server_url_errors_have_specific_text()
    {
        var localizer = new LocalizationService();
        localizer.Apply(AppLanguagePreference.English, "en-US");

        var https = AuthErrorText.Describe(
            localizer,
            new AuthError(AuthErrorKind.InvalidServer, "server.url_https_required"));

        Assert.Equal(localizer.T(StringKeys.ServerHttpsRequired), https);
    }

    [Fact]
    public void Sync_status_text_covers_offline_and_sign_in()
    {
        var localizer = new LocalizationService();
        localizer.Apply(AppLanguagePreference.English, "en-US");

        Assert.Equal(
            localizer.T(StringKeys.SyncOffline),
            SyncStatusText.Describe(localizer, new SyncStatusSnapshot(SyncStatusType.Offline, null, 0, null)));
        Assert.Equal(
            localizer.T(StringKeys.SyncNeedsSignIn),
            SyncStatusText.Describe(localizer, new SyncStatusSnapshot(SyncStatusType.NeedsSignIn, null, 0, null)));
        Assert.Equal(
            localizer.T(StringKeys.LastSyncedNever),
            SyncStatusText.LastSynced(localizer, null));
    }
}
