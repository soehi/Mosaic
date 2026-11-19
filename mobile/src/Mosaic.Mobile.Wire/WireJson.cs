using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mosaic.Mobile.Wire;

/// <summary>
/// The single JSON configuration for every <c>/v1</c> call. Unknown members are
/// ignored by System.Text.Json by default, which is the contract rule for the
/// mobile client: a server may add fields without breaking an older app.
/// </summary>
public static class WireJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
    }
}
