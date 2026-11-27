using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Mobile.Core.Projection;

/// <summary>
/// One task as the projection stores it. The raw payload JSON is kept verbatim
/// so a narrow write can change only the fields the plan names and preserve
/// everything else the desktop may have written (including fields this client
/// does not model).
/// </summary>
public sealed record SyncedTask
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string PayloadJson { get; init; }

    public required long Revision { get; init; }

    /// <summary>The <c>sync_items.server_revision</c> this payload was read at; 0 when unknown.</summary>
    public required long ServerRevision { get; init; }

    public required bool IsCompleted { get; init; }

    public DateTimeOffset? ArchivedAtUtc { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public bool IsArchived => ArchivedAtUtc is not null;
}

public sealed record TaskPayloadFields(
    Guid Id,
    string Title,
    bool IsCompleted,
    long Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? ArchivedAtUtc,
    DateTimeOffset? RestoredAtUtc);

/// <summary>Reads and mutates the desktop task payload without dropping unknown members.</summary>
public static class TaskPayload
{
    public static bool TryRead(string? payloadJson, out TaskPayloadFields fields, out string? error)
    {
        fields = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            error = "payload.missing";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "payload.not_object";
                return false;
            }

            if (!TryReadGuid(root, "id", out var id))
            {
                error = "payload.id";
                return false;
            }

            var title = ReadString(root, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                error = "payload.title";
                return false;
            }

            if (!root.TryGetProperty("revision", out var revisionElement) || !revisionElement.TryGetInt64(out var revision) || revision < 0)
            {
                error = "payload.revision";
                return false;
            }

            if (!TryReadTime(root, "createdAtUtc", out var created, required: true))
            {
                error = "payload.created";
                return false;
            }

            if (!TryReadTime(root, "updatedAtUtc", out var updated, required: true))
            {
                error = "payload.updated";
                return false;
            }

            _ = TryReadTime(root, "completedAtUtc", out var completed, required: false);
            _ = TryReadTime(root, "archivedAtUtc", out var archived, required: false);
            _ = TryReadTime(root, "restoredAtUtc", out var restored, required: false);

            fields = new TaskPayloadFields(
                id,
                title,
                ReadBool(root, "isCompleted"),
                revision,
                created!.Value,
                updated!.Value,
                completed,
                archived,
                restored);
            return true;
        }
        catch (JsonException)
        {
            error = "payload.invalid_json";
            return false;
        }
    }

    public static string SetComplete(string payloadJson, DateTimeOffset now)
    {
        var node = Parse(payloadJson);
        var revision = ReadRevision(node) + 1;
        node["isCompleted"] = true;
        node["completedAtUtc"] = Format(now);
        node["restoredAtUtc"] = null;
        node["updatedAtUtc"] = Format(now);
        node["revision"] = revision;
        return node.ToJsonString(JsonWriterOptions());
    }

    public static string SetArchived(string payloadJson, DateTimeOffset now)
    {
        var node = Parse(payloadJson);
        var revision = ReadRevision(node) + 1;
        node["archivedAtUtc"] = Format(now);
        node["updatedAtUtc"] = Format(now);
        node["revision"] = revision;
        return node.ToJsonString(JsonWriterOptions());
    }

    public static long ReadRevision(string payloadJson) => ReadRevision(Parse(payloadJson));

    public static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static JsonObject Parse(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var node = JsonNode.Parse(payloadJson) as JsonObject
            ?? throw new InvalidOperationException("Task payload is not a JSON object.");
        return node;
    }

    private static long ReadRevision(JsonObject node) =>
        node["revision"] is JsonValue value && value.TryGetValue<long>(out var revision) ? revision : 0;

    private static JsonSerializerOptions JsonWriterOptions() => Wire.WireJson.Options;

    private static bool TryReadGuid(JsonElement root, string name, out Guid id)
    {
        id = Guid.Empty;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out id);
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
        && (element.ValueKind == JsonValueKind.True
            || (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var parsed) && parsed));

    private static bool TryReadTime(JsonElement root, string name, out DateTimeOffset? value, bool required)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return !required;
        }

        if (element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            value = parsed;
            return true;
        }

        return !required;
    }
}