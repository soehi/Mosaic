using Mosaic.Contracts;
using Mosaic.Sync.Contracts;

namespace Mosaic.Sync.Http;

public sealed class HttpSyncModule : IModuleActivation, IDisposable
{
    private readonly HttpClient _client;
    private readonly HttpSyncTransport _transport;
    private bool _active;

    public HttpSyncModule(TimeProvider? clock = null)
    {
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        _transport = new(_client, clock);
    }

    public static ModuleManifest Definition { get; } = new(
        "sync-http",
        [new("sync.transport", 1)],
        [],
        "Provides the HTTPS client used for backup upload and restore endpoints.");

    public ModuleManifest Manifest => Definition;

    public ISyncTransport Transport => _active ? _transport : throw new InvalidOperationException("Backup transport is not active.");

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
