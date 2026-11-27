using Android.Content;
using Mosaic.Mobile.Core.Configuration;

namespace Mosaic.Mobile.App.Platform;

/// <summary>
/// The provisioned connection target. Provisioning itself is deferred by the
/// refactor plan; this implementation reads a private preference that an
/// out-of-band channel (or a future pairing surface) writes. Nothing in the
/// card renderer knows a server address.
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