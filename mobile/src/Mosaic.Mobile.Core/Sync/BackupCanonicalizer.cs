using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Core.Sync;

/// <summary>
/// Client-side counterpart of the server's backup canonicalizer
/// (<c>server/src/Mosaic.Server/Backups/JsonCanonicalizer.cs</c>): items are
/// sorted by kind then key (ordinal), object members are sorted by name, and
/// non-ASCII characters are not escaped. Verifying this digest proves the
/// downloaded snapshot is byte-for-byte the content the manifest advertises.
/// </summary>
public static class BackupCanonicalizer
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Serialize(IReadOnlyList<BackupItemDto> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var ordered = items
            .OrderBy(item => item.Kind ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.Key ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("items");
            foreach (var item in ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", item.Kind ?? string.Empty);
                writer.WriteString("key", item.Key ?? string.Empty);
                writer.WriteNumber("revision", item.Revision);
                writer.WritePropertyName("payload");
                if (item.Payload is { } payload)
                {
                    Write(payload, writer);
                }
                else
                {
                    writer.WriteNullValue();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static string ComputeSha256Hex(IReadOnlyList<BackupItemDto> items) =>
        Convert.ToHexString(SHA256.HashData(Serialize(items))).ToLowerInvariant();

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
                throw new InvalidOperationException("Backup content contains an unsupported JSON value.");
        }
    }
}