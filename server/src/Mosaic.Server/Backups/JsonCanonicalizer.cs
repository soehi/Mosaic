using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mosaic.Server.Backups;

/// <summary>
/// Deterministic JSON form used for backup content hashes: object properties are
/// sorted by name (ordinal), arrays keep their order, numbers keep their original
/// text and non-ASCII characters are not escaped. Documented in server/README.md
/// so the desktop client can compute the same digest.
/// </summary>
public static class JsonCanonicalizer
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            Write(element, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static byte[] SerializeBackupContent(IReadOnlyList<CanonicalItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("items");
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", item.Kind);
                writer.WriteString("key", item.Key);
                writer.WriteNumber("revision", item.Revision);
                writer.WritePropertyName("payload");
                writer.WriteRawValue(item.PayloadJson, skipInputValidation: false);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var value in element.EnumerateArray())
                {
                    Write(value, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new Http.ApiException(400, "request.invalid", "JSON 内容无效。");
        }
    }
}

public sealed record CanonicalItem(string Kind, string Key, long Revision, string PayloadJson);
