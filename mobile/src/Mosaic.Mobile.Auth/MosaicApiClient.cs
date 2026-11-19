using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Auth;

/// <summary>Supplies the in-memory access token and performs the single shared refresh.</summary>
public interface IAccessTokenProvider
{
    string? AccessToken { get; }

    /// <summary>Refresh once; returns null when refreshing is impossible or the session ended.</summary>
    Task<string?> RefreshAsync(CancellationToken cancellationToken);
}

public sealed record MobileClientInfo(string AppVersion, string Platform, string OsVersion)
{
    /// <summary>Product/version and OS only: never an email or hardware identifier.</summary>
    public string UserAgent => $"Mosaic-Mobile/{AppVersion} ({Platform} {OsVersion})";
}

/// <summary>
/// The one HTTP owner for every <c>/v1</c> call. Pages never build URLs or read
/// tokens; Auth and Sync both go through this client, which attaches the bearer
/// token, refreshes once on 401, and applies the transport's retry rules.
/// </summary>
public sealed class MosaicApiClient : IMosaicApi, IDisposable
{
    public const int MaxAttempts = 4;

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public MosaicApiClient(
        HttpClient http,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        bool ownsHttp = true)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _ownsHttp = ownsHttp;
        _delay = delay ?? ((time, ct) => Task.Delay(time, ct));
    }

    /// <summary>Server base URL without a trailing slash and without <c>/v1</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public IAccessTokenProvider? TokenProvider { get; set; }

    public static HttpClient CreateHttpClient(MobileClientInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(info.UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public Task<ApiResult<T>> GetAsync<T>(
        string relativePath,
        bool authenticated = true,
        bool retryTransient = false,
        CancellationToken cancellationToken = default) =>
        SendAsync<T>(HttpMethod.Get, relativePath, null, authenticated, retryTransient, cancellationToken);

    /// <summary>
    /// Probe a candidate server before it becomes <see cref="BaseUrl"/>. Same
    /// client, same parsing, no bearer token.
    /// </summary>
    public Task<ApiResult<T>> GetFromAsync<T>(
        string baseUrl,
        string relativePath,
        CancellationToken cancellationToken = default) =>
        SendAsync<T>(HttpMethod.Get, relativePath, null, false, false, cancellationToken, baseUrl);

    public Task<ApiResult<T>> PostFromAsync<T>(
        string baseUrl,
        string relativePath,
        object? body,
        CancellationToken cancellationToken = default) =>
        SendAsync<T>(HttpMethod.Post, relativePath, body, false, false, cancellationToken, baseUrl);

    public Task<ApiResult<T>> PostAsync<T>(
        string relativePath,
        object? body,
        bool authenticated = true,
        bool retryTransient = false,
        CancellationToken cancellationToken = default) =>
        SendAsync<T>(HttpMethod.Post, relativePath, body, authenticated, retryTransient, cancellationToken);

    public Task<ApiResult> PostAsync(
        string relativePath,
        object? body,
        bool authenticated = true,
        bool retryTransient = false,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, relativePath, body, authenticated, retryTransient, cancellationToken);

    public Task<ApiResult> DeleteAsync(
        string relativePath,
        bool authenticated = true,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, relativePath, null, authenticated, false, cancellationToken);

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        bool authenticated,
        bool retryTransient,
        CancellationToken cancellationToken,
        string? baseUrlOverride = null)
    {
        var outcome = await ExchangeAsync(
                method,
                relativePath,
                body,
                authenticated,
                retryTransient,
                cancellationToken,
                baseUrlOverride)
            .ConfigureAwait(false);
        if (outcome.Failure is not null)
        {
            return ApiResult<T>.Fail(outcome.StatusCode, outcome.Failure, outcome.RetryAfter);
        }

        if (string.IsNullOrWhiteSpace(outcome.Body))
        {
            return ApiResult<T>.Ok(default!, outcome.StatusCode);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(outcome.Body, WireJson.Options);
            return ApiResult<T>.Ok(value!, outcome.StatusCode);
        }
        catch (JsonException)
        {
            return ApiResult<T>.Fail(
                outcome.StatusCode,
                new WireErrorDto
                {
                    Code = LocalTransportCodes.Protocol,
                    Message = "服务器响应格式无法识别。",
                });
        }
    }

    private async Task<ApiResult> SendAsync(
        HttpMethod method,
        string relativePath,
        object? body,
        bool authenticated,
        bool retryTransient,
        CancellationToken cancellationToken)
    {
        var outcome = await ExchangeAsync(method, relativePath, body, authenticated, retryTransient, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Failure is null
            ? ApiResult.Ok(outcome.StatusCode)
            : ApiResult.Fail(outcome.StatusCode, outcome.Failure, outcome.RetryAfter);
    }

    private async Task<ExchangeOutcome> ExchangeAsync(
        HttpMethod method,
        string relativePath,
        object? body,
        bool authenticated,
        bool retryTransient,
        CancellationToken cancellationToken,
        string? baseUrlOverride = null)
    {
        var baseUrl = string.IsNullOrWhiteSpace(baseUrlOverride) ? BaseUrl : baseUrlOverride;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return ExchangeOutcome.Failed(0, ProtocolError("尚未配置服务器地址。"));
        }

        var uri = BuildUri(baseUrl, relativePath);
        var json = body is null
            ? null
            : JsonSerializer.Serialize(body, body.GetType(), WireJson.Options);

        var attempt = 0;
        var refreshed = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(method, uri);
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            if (authenticated && TokenProvider is not null)
            {
                var token = TokenProvider.AccessToken
                    ?? await TokenProvider.RefreshAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                if (!retryTransient || attempt >= MaxAttempts)
                {
                    return ExchangeOutcome.Failed(0, TimeoutError());
                }

                await _delay(Backoff(attempt++), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException)
            {
                if (!retryTransient || attempt >= MaxAttempts)
                {
                    return ExchangeOutcome.Failed(0, NetworkError());
                }

                await _delay(Backoff(attempt++), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized
                    && authenticated
                    && !refreshed
                    && TokenProvider is not null)
                {
                    refreshed = true;
                    var token = await TokenProvider.RefreshAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(token))
                    {
                        continue;
                    }
                }

                var retryAfter = ReadRetryAfter(response);
                if (retryTransient
                    && method == HttpMethod.Get
                    && attempt < MaxAttempts
                    && IsTransient(response.StatusCode))
                {
                    await _delay(retryAfter ?? Backoff(attempt++), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return ExchangeOutcome.Success((int)response.StatusCode, text);
                }

                var error = WireErrorDto.TryParse(text)
                    ?? new WireErrorDto
                    {
                        Code = response.StatusCode == HttpStatusCode.NotFound
                            ? "server.not_found"
                            : "http." + (int)response.StatusCode,
                    };
                return ExchangeOutcome.Failed((int)response.StatusCode, error, retryAfter);
            }
        }
    }

    private static Uri BuildUri(string baseUrl, string relativePath) =>
        new($"{baseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}", UriKind.Absolute);

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status >= 500;

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(Math.Pow(2, attempt + 1), MaxBackoff.TotalSeconds);
        var jitter = 0.75 + (Random.Shared.NextDouble() * 0.5);
        return TimeSpan.FromSeconds(Math.Min(seconds * jitter, MaxBackoff.TotalSeconds));
    }

    private static WireErrorDto ProtocolError(string message) =>
        new() { Code = LocalTransportCodes.Protocol, Message = message };

    private static WireErrorDto NetworkError() =>
        new() { Code = LocalTransportCodes.Network, Message = "无法连接服务器。" };

    private static WireErrorDto TimeoutError() =>
        new() { Code = LocalTransportCodes.Timeout, Message = "请求超时。" };

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private sealed record ExchangeOutcome(int StatusCode, string? Body, WireErrorDto? Failure, TimeSpan? RetryAfter)
    {
        public static ExchangeOutcome Success(int statusCode, string body) =>
            new(statusCode, body, null, null);

        public static ExchangeOutcome Failed(int statusCode, WireErrorDto error, TimeSpan? retryAfter = null) =>
            new(statusCode, null, error, retryAfter);
    }
}
