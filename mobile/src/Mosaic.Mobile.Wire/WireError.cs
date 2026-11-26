using System.Text.Json;

namespace Mosaic.Mobile.Wire;

/// <summary>
/// The frozen error envelope <c>{code,message,traceId,details?}</c>. The server
/// also mirrors <c>details</c> members at the root for older clients, so
/// <see cref="Details"/> is populated from <c>details</c> when present and from
/// the root object otherwise. Only <see cref="Code"/> drives behaviour.
/// </summary>
public sealed record WireError(string Code, string? Message, string? TraceId, JsonElement? Details)
{
    public T? DetailsAs<T>() => Details is { } element ? element.Deserialize<T>(WireJson.Options) : default;

    public static WireError? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;
            var code = ReadString(root, "code");
            if (string.IsNullOrWhiteSpace(code))
            {
                return null;
            }

            var details = root.TryGetProperty("details", out var nested)
                && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;

            return new WireError(code, ReadString(root, "message"), ReadString(root, "traceId"), details.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public static class WireErrorCodes
{
    public const string AuthRequired = "auth.required";
    public const string AuthInvalidRefresh = "auth.invalid_refresh";
    public const string AuthRefreshReused = "auth.refresh_reused";
    public const string AuthSessionExpired = "auth.session_expired";
    public const string AuthSessionRevoked = "auth.session_revoked";
    public const string AuthInvalidCredentials = "auth.invalid_credentials";
    public const string AuthRateLimited = "auth.rate_limited";

    public const string DeviceInvalidCode = "device.invalid_code";
    public const string DeviceRateLimited = "device.rate_limited";

    public const string SyncConflict = "sync.conflict";
    public const string SyncInvalid = "sync.invalid";
    public const string SyncSnapshotRequired = "sync.snapshot_required";

    public const string BackupNotFound = "backup.not_found";
    public const string BackupInvalid = "backup.invalid";
    public const string BackupHashMismatch = "backup.hash_mismatch";

    public const string RequestInvalid = "request.invalid";

    public const string ClientResponseTooLarge = "client.response_too_large";
}