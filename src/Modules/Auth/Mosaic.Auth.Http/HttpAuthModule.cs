using Mosaic.Auth.Contracts;
using Mosaic.Contracts;

namespace Mosaic.Auth.Http;

/// <summary>
/// Provides the HTTPS transport used by the account core. Activation performs no network work so
/// an unreachable or unconfigured server never delays startup. Redirects stay disabled so tokens
/// are only ever sent to the configured origin.
/// </summary>
public sealed class HttpAuthModule : IModuleActivation, IDisposable
{
    private readonly HttpClient _client;
    private readonly HttpAuthTransport _transport;
    private bool _active;

    public HttpAuthModule(TimeProvider? clock = null)
    {
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _transport = new(_client, clock);
    }

    public static ModuleManifest Definition { get; } = new(
        "auth-http",
        [new("auth.transport", 1)],
        [],
        "Provides the HTTPS client used for account, device and backup endpoints.");

    public ModuleManifest Manifest => Definition;

    public IAuthTransport Transport => _active ? _transport : throw new InvalidOperationException("Account transport is not active.");

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        _active = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _active = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _active = false;
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _client.Dispose();
}
