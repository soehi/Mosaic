namespace Mosaic.Mobile.Presentation;

/// <summary>Device-level language choice. This is a presentation preference,
/// not a setting synced with the server; the Sync layer no longer owns it.</summary>
public enum AppLanguagePreference
{
    FollowSystem = 0,
    Chinese,
    English,
}

/// <summary>Device-level light/dark choice, likewise owned by Presentation.</summary>
public enum ThemePreference
{
    FollowSystem = 0,
    Light,
    Dark,
}
