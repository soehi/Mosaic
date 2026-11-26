using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync.Tests;

internal sealed record RecordedRequest(
    HttpMethod Method,
    string Uri,
    string? Body,
    string? Authorization);

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, Task<HttpResponseMessage>> _responder;

    public StubHandler(Func<RecordedRequest, HttpResponseMessage> responder)
    {
        _responder = request => Task.FromResult(responder(request));
    }

    private StubHandler(Func<RecordedRequest, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public static StubHandler FromAsync(Func<RecordedRequest, Task<HttpResponseMessage>> responder) =>
        new(responder);

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>The token the HTTP stack handed to the handler (for cancellation tests).</summary>
    public CancellationToken Token { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Token = cancellationToken;
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!.ToString(),
            body,
            request.Headers.Authorization?.ToString());
        Requests.Add(recorded);
        return await _responder(recorded);
    }
}

internal static class Responses
{
    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(
        HttpStatusCode status,
        string code,
        object? details = null) =>
        Json(status, WireJson.Serialize(new WireErrorDto
        {
            Code = code,
            Details = details is null
                ? null
                : JsonSerializer.SerializeToElement(details, WireJson.Options),
        }));

    public static HttpResponseMessage Changes(string body) => Json(HttpStatusCode.OK, body);

    public static HttpResponseMessage Bytes(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
}

internal sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    public TempDatabase()
    {
        _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mosaic-mobile-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var identity = ProjectionIdentity.Create("https://example.test", "test-user");
        Database = new MobileDatabase(System.IO.Path.Combine(_directory, "mobile.db"));
        Database.InitializeAsync(identity).GetAwaiter().GetResult();
        Store = new SqliteSyncStore(Database, identity);
    }

    public MobileDatabase Database { get; }

    public SqliteSyncStore Store { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled SQLite handle may briefly hold the file; temp cleanup
            // is best effort and must not fail the test.
        }
    }
}

internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now += amount;
}

internal static class TaskJson
{
    public static string Build(
        string id,
        string title = "Task",
        bool completed = false,
        string? archivedAt = null,
        long revision = 1,
        string updated = "2026-11-19T09:00:00Z")
    {
        var archived = archivedAt is null ? "null" : $"\"{archivedAt}\"";
        return $$"""
            {
              "id": "{{id}}",
              "title": "{{title}}",
              "notes": "",
              "isCompleted": {{(completed ? "true" : "false")}},
              "createdAtUtc": "2026-11-19T08:00:00Z",
              "updatedAtUtc": "{{updated}}",
              "revision": {{revision}},
              "archivedAtUtc": {{archived}}
            }
            """;
    }

    /// <summary>
    /// Canonical 0.1.1 change row: <c>deleted</c> plus <c>createdAt</c> are the
    /// authority; the legacy <c>operation</c>/<c>updatedAtUtc</c> pair is only a
    /// fallback and must not appear here, so the strict projector is exercised.
    /// </summary>
    public static string Change(
        string id,
        string title = "Task",
        long serverRevision = 1,
        bool deleted = false,
        bool completed = false,
        string? archivedAt = null,
        long revision = 1) =>
        deleted
            ? $$"""
              {"serverRevision":{{serverRevision}},"kind":"task","key":"{{id}}","deleted":true,"createdAt":"2026-11-19T09:00:00Z"}
              """
            : $$"""
              {"serverRevision":{{serverRevision}},"kind":"task","key":"{{id}}","deleted":false,"createdAt":"2026-11-19T09:00:00Z","payload":{{Build(id, title, completed, archivedAt, revision)}}}
              """;

    /// <summary>
    /// A setting row: still valid on the wire, never projected by the mobile
    /// client. The canonical shape is used so the projector's skip branch runs.
    /// </summary>
    public static string Setting(string key, string valueJson, long serverRevision = 1) =>
        $$"""
          {"serverRevision":{{serverRevision}},"kind":"setting","key":"{{key}}","deleted":false,"createdAt":"2026-11-19T09:00:00Z","payload":{"value":{{valueJson}} } }
          """;
}

internal static class SnapshotJson
{
    public const string Id = "snap-0001";
    public const string Cursor = "snap-cursor-1";
    public const string CreatedAt = "2026-11-19T10:00:00Z";

    /// <summary>The verified snapshot body as the server would serve it.</summary>
    public static string Body(
        string snapshotId = Id,
        string cursor = Cursor,
        long serverRevision = 40,
        params (string Id, string Title)[] tasks) =>
        WireJson.Serialize(new BackupSnapshotDto
        {
            SnapshotId = snapshotId,
            Cursor = cursor,
            ServerRevision = serverRevision,
            ItemCount = tasks.Length,
            CreatedAt = DateTimeOffset.Parse(CreatedAt, System.Globalization.CultureInfo.InvariantCulture),
            Items =
            [
                .. tasks.Select(task => new BackupItemDto
                {
                    Kind = "task",
                    Key = task.Id,
                    Revision = 1,
                    Payload = JsonSerializer.SerializeToElement(
                        new TaskItemDto
                        {
                            Id = Guid.Parse(task.Id),
                            Title = task.Title,
                            CreatedAtUtc = new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero),
                            UpdatedAtUtc = new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero),
                            Revision = 1,
                        },
                        WireJson.Options),
                }),
            ],
        });

    /// <summary>
    /// The manifest for a body. <paramref name="contentSha256"/> and
    /// <paramref name="sizeBytes"/> default to the body's real values; pass an
    /// override to build a manifest that disagrees with its body.
    /// </summary>
    public static string Manifest(
        string body,
        string snapshotId = Id,
        string cursor = Cursor,
        long serverRevision = 40,
        int itemCount = 1,
        long? sizeBytes = null,
        string? contentSha256 = null) =>
        WireJson.Serialize(new BackupManifestDto
        {
            SnapshotId = snapshotId,
            Cursor = cursor,
            ServerRevision = serverRevision,
            ItemCount = itemCount,
            SizeBytes = sizeBytes ?? Encoding.UTF8.GetByteCount(body),
            ContentSha256 = contentSha256 ?? LowerHexSha256(body),
            CreatedAt = DateTimeOffset.Parse(CreatedAt, System.Globalization.CultureInfo.InvariantCulture),
        });

    public static string LowerHexSha256(string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}

internal static class Ids
{
    public static readonly string One = "11111111-1111-1111-1111-111111111111";
    public static readonly string Two = "22222222-2222-2222-2222-222222222222";
    public static readonly string Three = "33333333-3333-3333-3333-333333333333";
}
