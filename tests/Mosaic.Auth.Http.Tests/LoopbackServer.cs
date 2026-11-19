using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mosaic.Auth.Http.Tests;

// Minimal HTTP/1.1 loopback endpoint used to exercise the wire adapters without a real server.
internal sealed class LoopbackServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopped = new();
    private readonly Task _loop;

    public LoopbackServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port { get; }
    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");
    public Func<HttpListenerRequest, string, (int Status, string Body)> Handler { get; set; } =
        (_, _) => (200, "{}");
    public List<Recorded> Requests { get; } = [];

    public sealed record Recorded(string Method, string Path, string Body, string? Authorization);

    public static Uri ClosedUri()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new Uri($"http://127.0.0.1:{port}/");
    }

    private async Task AcceptAsync()
    {
        while (!_stopped.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) when (_stopped.IsCancellationRequested) { return; }
            catch (HttpListenerException) { return; }
            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        lock (Requests)
            Requests.Add(new(context.Request.HttpMethod, context.Request.Url!.AbsolutePath,
                body, context.Request.Headers["Authorization"]));
        var (status, payload) = Handler(context.Request, body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(payload);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _stopped.Cancel();
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        try { await _loop; } catch (Exception) { }
        _listener.Close();
        _stopped.Dispose();
    }
}
