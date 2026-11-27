using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Core.Api;

/// <summary>
/// HTTP transport for the <c>/v1</c> API. Only the configured base URI is used;
/// redirects are refused so an HTTPS origin cannot be downgraded, and no
/// certificate callback is ever installed (a failing certificate ends the
/// request, it never becomes a "continue anyway" prompt).
/// </summary>
public sealed class HttpMosaicApi : IMosaicApi, IDisposable
{
    private const int MaxErrorBodyBytes = 64 * 1024;

    private readonly HttpClient _http;

    public HttpMosaicApi(Uri baseUri, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        _http = handler is null ? new HttpClient(CreateDefaultHandler(), disposeHandler: true) : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = baseUri;
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ApiResponse<T>> GetAsync<T>(string path, string? accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await SendAsync<T>(request, accessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiResponse<T>> PostAsync<T>(string path, object body, string? accessToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, WireJson.Options), Encoding.UTF8, "application/json"),
        };
        return await SendAsync<T>(request, accessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiResponse<byte[]>> GetBytesAsync(
        string path,
        long maxBytes,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrEmpty(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                return ApiResponse<byte[]>.From((int)response.StatusCode, null, error);
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared is { } length && length > maxBytes)
            {
                return ApiResponse<byte[]>.From(413, null, TooLarge());
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maxBytes)
                {
                    return ApiResponse<byte[]>.From(413, null, TooLarge());
                }

                buffer.Write(chunk, 0, read);
            }

            return ApiResponse<byte[]>.From((int)response.StatusCode, buffer.ToArray(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApiResponse<byte[]>.Cancelled();
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            return ApiResponse<byte[]>.TransportFailure();
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<ApiResponse<T>> SendAsync<T>(
        HttpRequestMessage request,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    return ApiResponse<T>.From((int)response.StatusCode, default, null);
                }

                var value = await response.Content
                    .ReadFromJsonAsync<T>(WireJson.Options, cancellationToken)
                    .ConfigureAwait(false);
                if (value is null)
                {
                    return ApiResponse<T>.From(0, default, null);
                }

                return ApiResponse<T>.From((int)response.StatusCode, value, null);
            }

            var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return ApiResponse<T>.From((int)response.StatusCode, default, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApiResponse<T>.Cancelled();
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            return ApiResponse<T>.TransportFailure();
        }
    }

    private static async Task<WireError?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[MaxErrorBodyBytes];
            var read = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
            return read == 0 ? null : WireError.TryParse(new string(buffer, 0, read));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    private static WireError TooLarge() =>
        new(WireErrorCodes.ClientResponseTooLarge, null, null, null);

    private static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}

public sealed class HttpMosaicApiFactory : IMosaicApiFactory, IDisposable
{
    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan _timeout;

    public HttpMosaicApiFactory(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _handler = handler;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public IMosaicApi Create(Uri baseUri) => new HttpMosaicApi(baseUri, _handler, _timeout);

    public void Dispose()
    {
        // Handler ownership stays with the caller that supplied it.
    }
}