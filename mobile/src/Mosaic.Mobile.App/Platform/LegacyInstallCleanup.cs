using Android.Content;

namespace Mosaic.Mobile.App.Platform;

/// <summary>
/// One-time retirement of data written by the deleted 0.2.x app. The refactor
/// plan says explicitly that old projections, tokens and widget state are not
/// migrated: this class retires them without ever reading them.
/// <list type="bullet">
/// <item><c>files/projections/</c> (schema v2) is moved to a retired name.</item>
/// <item><c>mosaic_secure</c> preferences (old token records) are deleted.</item>
/// <item>The old Keystore alias is removed so its ciphertext can never be read.</item>
/// </list>
/// </summary>
public static class LegacyInstallCleanup
{
    private const string OldPreferencesFile = "mosaic_secure";

    private const string OldProjectionDirectory = "projections";

    private const string MarkerKey = "mosaic.mobile.v3.cleanup";

    public static void Run(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var preferences = context.GetSharedPreferences(AndroidSecretStorage.PreferencesFile, FileCreationMode.Private);
        if (preferences?.GetBoolean(MarkerKey, false) == true)
        {
            return;
        }

        RetireOldProjections(context);
        try
        {
            context.DeleteSharedPreferences(OldPreferencesFile);
        }
        catch (Exception exception) when (exception is Java.Lang.Exception)
        {
            // Best effort: the new client uses different preference keys anyway.
        }

        AndroidSecretStorage.PurgeLegacyKey();
        preferences?.Edit()?.PutBoolean(MarkerKey, true)?.Commit();
    }

    private static void RetireOldProjections(Context context)
    {
        var filesDirectory = context.FilesDir?.AbsolutePath;
        if (string.IsNullOrEmpty(filesDirectory))
        {
            return;
        }

        var oldDirectory = Path.Combine(filesDirectory, OldProjectionDirectory);
        if (!Directory.Exists(oldDirectory))
        {
            return;
        }

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var retired = Path.Combine(filesDirectory, $"retired-projections-v2-{stamp}");
        try
        {
            Directory.Move(oldDirectory, retired);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}