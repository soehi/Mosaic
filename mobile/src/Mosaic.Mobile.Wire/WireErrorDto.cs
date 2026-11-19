using System.Text.Json;

namespace Mosaic.Mobile.Wire;

/// <summary>
/// The shared error envelope: <c>{code,message,traceId,details?}</c>. Only
/// <see cref="Code"/> drives behaviour; <see cref="Message"/> is a fallback for
/// display and must never be pattern-matched.
/// </summary>
public sealed record WireErrorDto
{
    public string Code { get; init; } = string.Empty;

    public string? Message { get; init; }

    public string? TraceId { get; init; }

    public JsonElement? Details { get; init; }

    /// <summary>Parses an error body, returning <see langword="null"/> when it is not an envelope.</summary>
    public static WireErrorDto? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var parsed = WireJson.Deserialize<WireErrorDto>(body);
            return string.IsNullOrWhiteSpace(parsed?.Code) ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads <c>details.requiresSnapshot</c> when the server sends it.</summary>
    public bool RequiresSnapshot =>
        Details is { ValueKind: JsonValueKind.Object } details
        && details.TryGetProperty("requiresSnapshot", out var flag)
        && flag.ValueKind is JsonValueKind.True;

    /// <summary>
    /// Reads a typed <c>details</c> payload (for example <c>details.current</c>
    /// on a sync conflict). Returns null for a missing or unreadable payload.
    /// </summary>
    public T? DetailsAs<T>()
        where T : class
    {
        if (Details is not { } details
            || details.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        try
        {
            return details.Deserialize<T>(WireJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
