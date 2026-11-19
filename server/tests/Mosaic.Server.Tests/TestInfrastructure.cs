using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mosaic.Server.Emailing;

namespace Mosaic.Server.Tests;

public sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration) => _now = _now.Add(duration);
}

public sealed class FakeEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, EmailSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured)
        {
            throw new EmailUnavailableException("email.not_configured");
        }

        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public string? LastCodeFor(string email)
    {
        var message = Sent.LastOrDefault(item => string.Equals(item.To, email, StringComparison.OrdinalIgnoreCase));
        if (message is null)
        {
            return null;
        }

        var match = Regex.Match(message.TextBody, @"\b(\d{6})\b");
        return match.Success ? match.Groups[1].Value : null;
    }

    public int CountFor(string email) =>
        Sent.Count(item => string.Equals(item.To, email, StringComparison.OrdinalIgnoreCase));
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = category + " " + formatter(state, exception);
            if (exception is not null)
            {
                line += " | " + exception.GetType().Name + ": " + exception.Message;
            }

            owner.Messages.Enqueue(line);
        }
    }
}

/// <summary>
/// Starts a real Kestrel instance on an ephemeral loopback port with a temporary
/// SQLite database, a deterministic clock and an in-memory mail transport.
/// </summary>
public sealed class TestServerHost : IAsyncDisposable
{
    public const string DefaultPassword = "correct horse battery staple";

    private TestServerHost()
    {
    }

    public MutableTimeProvider Clock { get; } = new();

    public FakeEmailSender Email { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public ServerOptions Options { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public WebApplication App { get; private set; } = null!;

    public string DataDirectory { get; private set; } = string.Empty;

    private bool _deleteDirectoryOnDispose;

    public static async Task<TestServerHost> StartAsync(
        Action<ServerOptions>? configure = null,
        string? dataDirectory = null)
    {
        var host = new TestServerHost();
        host.DataDirectory = dataDirectory ?? Path.Combine(
            Path.GetTempPath(),
            "mosaic-server-tests",
            Guid.NewGuid().ToString("N"));
        host._deleteDirectoryOnDispose = dataDirectory is null;
        Directory.CreateDirectory(host.DataDirectory);

        var options = new ServerOptions
        {
            DataDirectory = host.DataDirectory,
            PublicUrl = "http://127.0.0.1",
            ServerName = "Mosaic Test",
            MasterKey = RandomNumberGenerator.GetBytes(32),
            TimeProvider = host.Clock,
            EmailSenderOverride = host.Email,
            LogProvider = host.Logs,
            Email = new EmailSettings
            {
                Host = "smtp.test",
                Port = 587,
                Security = EmailSecurity.StartTls,
                From = "noreply@test.local",
                FromName = "Mosaic Test",
            },
        };
        configure?.Invoke(options);
        host.Options = options;

        host.App = await ServerApplication.CreateAsync(["--urls", "http://127.0.0.1:0"], options);
        await host.App.StartAsync();
        var addresses = host.App.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        host.Client = new HttpClient
        {
            BaseAddress = new Uri(addresses.First()),
            Timeout = TimeSpan.FromSeconds(60),
        };
        return host;
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string? token = null,
        string? adminToken = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (adminToken is not null)
        {
            request.Headers.Add("X-Admin-Token", adminToken);
        }

        return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null, string? token = null) =>
        SendAsync(HttpMethod.Post, path, body, token);

    public Task<HttpResponseMessage> PutAsync(string path, object? body, string? token = null) =>
        SendAsync(HttpMethod.Put, path, body, token);

    public Task<HttpResponseMessage> GetAsync(string path, string? token = null) =>
        SendAsync(HttpMethod.Get, path, token: token);

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public static string? CodeOf(JsonElement element) =>
        element.TryGetProperty("code", out var code) ? code.GetString() : null;

    public static string? ErrorCode(JsonElement element) => CodeOf(element);

    public async Task<(string Email, string Password, JsonElement Session)> RegisterAsync(
        string? email = null,
        string password = DefaultPassword)
    {
        email ??= "user-" + Guid.NewGuid().ToString("N") + "@example.com";
        var code = await RequestCodeAsync(email, "register");
        var response = await PostAsync("/v1/auth/register", new
        {
            email,
            password,
            code,
            deviceLabel = "test",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (email, password, await JsonAsync(response));
    }

    public async Task<string> RequestCodeAsync(string email, string purpose = "register")
    {
        var response = await PostAsync("/v1/auth/email-code", new { email, purpose });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return Email.LastCodeFor(email)
            ?? throw new InvalidOperationException($"未向 {email} 发送验证码。");
    }

    public static string AccessToken(JsonElement session) =>
        session.GetProperty("accessToken").GetString()!;

    public static string RefreshToken(JsonElement session) =>
        session.GetProperty("refreshToken").GetString()!;

    public string DatabasePath => Options.DatabasePath!;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await App.StopAsync(timeout.Token);
        await App.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (!_deleteDirectoryOnDispose)
        {
            return;
        }

        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
