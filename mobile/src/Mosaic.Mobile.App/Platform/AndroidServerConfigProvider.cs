using Android.Content;
using Mosaic.Mobile.Core.Configuration;

namespace Mosaic.Mobile.App.Platform;

/// <summary>
/// Stores the connection target in private preferences. The pairing activity
/// writes it and the widget kernel reads it before restoring a session.
/// </summary>
public sealed class AndroidServerConfigProvider : IServerConfigProvider
{
    public const string PreferenceFile = "mosaic.mobile.v3.config";

    public const string ServerUrlKey = "serverUrl";

    public const string DeviceLabelKey = "deviceLabel";

    private readonly ISharedPreferences _preferences;

    public AndroidServerConfigProvider(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _preferences = context.GetSharedPreferences(PreferenceFile, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Configuration preferences are unavailable.");
    }

    public bool Save(ServerConfiguration configuration) => _preferences.Edit()
        ?.PutString(ServerUrlKey, configuration.NormalizedOrigin)
        ?.PutString(DeviceLabelKey, configuration.DeviceLabel)
        ?.Commit() == true;

    public Task<ServerConfiguration?> GetAsync(CancellationToken cancellationToken)
    {
        var raw = _preferences.GetString(ServerUrlKey, null);
        if (!ServerAddress.TryNormalize(raw, out var configuration))
        {
            return Task.FromResult<ServerConfiguration?>(null);
        }

        var label = _preferences.GetString(DeviceLabelKey, null);
        return Task.FromResult<ServerConfiguration?>(
            string.IsNullOrWhiteSpace(label) ? configuration : configuration with { DeviceLabel = label });
    }
}