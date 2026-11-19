using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Auth.Tests;

internal sealed record RecordedRequest(
    HttpMethod Method,
    string Uri,
    string? Body,
    string? Authorization);

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, Task<HttpResponseMessage>> _responder;

    public StubHandler(Func<RecordedRequest, HttpResponseMessage> responder)
        : this(request => Task.FromResult(responder(request)))
    {
    }

    public StubHandler(Func<RecordedRequest, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
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

    public static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);

    public static HttpResponseMessage Error(HttpStatusCode status, string code, string? message = null) =>
        Json(status, WireJson.Serialize(new WireErrorDto { Code = code, Message = message }));

    public static HttpResponseMessage RateLimited(string code, int seconds)
    {
        var response = Error((HttpStatusCode)429, code);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    public static string Session(
        string access = "access-1",
        string refresh = "refresh-1",
        DateTimeOffset? accessExpires = null,
        DateTimeOffset? refreshExpires = null,
        string email = "user@example.com") =>
        WireJson.Serialize(new SessionDto
        {
            AccessToken = access,
            RefreshToken = refresh,
            AccessTokenExpiresAt = accessExpires ?? DateTimeOffset.UtcNow.AddHours(1),
            RefreshTokenExpiresAt = refreshExpires ?? DateTimeOffset.UtcNow.AddDays(30),
            User = new UserDto
            {
                Id = "user-1",
                Email = email,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            },
        });
}

internal sealed class InMemorySecretStorage : ISecretStorage
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    public bool FailReads { get; set; }

    public bool FailWrites { get; set; }

    public int Deletes { get; private set; }

    public IReadOnlyDictionary<string, byte[]> Values => _values;

    public byte[]? Find(string keyPrefix) =>
        _values.FirstOrDefault(pair => pair.Key.StartsWith(keyPrefix, StringComparison.Ordinal)).Value;

    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        if (FailReads)
        {
            throw new SessionStoreUnavailableException("test read failure");
        }

        return Task.FromResult(_values.TryGetValue(key, out var value) ? value.ToArray() : null);
    }

    public Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            throw new SessionStoreUnavailableException("test write failure");
        }

        _values[key] = value.ToArray();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Deletes++;
        _values.Remove(key);
        return Task.CompletedTask;
    }

    public void Corrupt(string keyPrefix)
    {
        var key = _values.Keys.FirstOrDefault(k => k.StartsWith(keyPrefix, StringComparison.Ordinal));
        if (key is not null)
        {
            _values[key] = Encoding.UTF8.GetBytes("{ not a session");
        }
    }
}

internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public TestTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public TestTimeProvider()
        : this(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now += amount;
}

internal static class TestApi
{
    public static MosaicApiClient Create(StubHandler handler, string baseUrl = "https://example.test")
    {
        var client = new MosaicApiClient(new HttpClient(handler)) { BaseUrl = baseUrl };
        return client;
    }
}
