using System.Net;
using System.Net.Http.Headers;
using System.Text;
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

    public static HttpResponseMessage Error(HttpStatusCode status, string code) =>
        Json(status, WireJson.Serialize(new WireErrorDto { Code = code }));

    public static HttpResponseMessage Changes(string body) => Json(HttpStatusCode.OK, body);
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
        Database = new MobileDatabase(System.IO.Path.Combine(_directory, "mobile.db"));
        Database.InitializeAsync().GetAwaiter().GetResult();
        Store = new SqliteSyncStore(Database);
    }

    public MobileDatabase Database { get; }

    public SqliteSyncStore Store { get; }

    public void Dispose()
    {
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
              {"serverRevision":{{serverRevision}},"kind":"task","key":"{{id}}","operation":"delete","updatedAtUtc":"2026-11-19T09:00:00Z"}
              """
            : $$"""
              {"serverRevision":{{serverRevision}},"kind":"task","key":"{{id}}","operation":"upsert","payload":{{Build(id, title, completed, archivedAt, revision)}},"updatedAtUtc":"2026-11-19T09:00:00Z"}
              """;

    public static string Setting(string key, string valueJson, long serverRevision = 1) =>
        $$"""
          {"serverRevision":{{serverRevision}},"kind":"setting","key":"{{key}}","operation":"upsert","payload":{"value":{{valueJson}}},"updatedAtUtc":"2026-11-19T09:00:00Z"}
          """;
}

internal static class Ids
{
    public static readonly string One = "11111111-1111-1111-1111-111111111111";
    public static readonly string Two = "22222222-2222-2222-2222-222222222222";
    public static readonly string Three = "33333333-3333-3333-3333-333333333333";
}
