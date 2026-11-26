using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mosaic.Mobile.Wire;

/// <summary>
/// The single JSON configuration for every <c>/v1</c> call. Web defaults give
/// camelCase members and case-insensitive reads; unlisted members are ignored,
/// which is the contract rule for the mobile client: a server may add fields
/// without breaking an older app.
/// </summary>
public static class WireJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}