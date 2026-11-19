using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Mosaic.Server.Emailing;

/// <summary>
/// Minimal SMTP client with mandatory TLS 1.2+ (implicit TLS or STARTTLS),
/// AUTH PLAIN/LOGIN and multipart UTF-8 messages. Failures are reported to
/// callers as a generic 503; the SMTP response text is never echoed back.
/// </summary>
public sealed class SmtpEmailSender(ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task SendAsync(EmailMessage message, EmailSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsConfigured)
        {
            throw new EmailUnavailableException("email.not_configured");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var token = timeout.Token;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(settings.Host, settings.Port, token).ConfigureAwait(false);
            var stream = (Stream)client.GetStream();
            if (settings.Security == EmailSecurity.SslOnConnect)
            {
                stream = await UpgradeAsync(stream, settings.Host, token).ConfigureAwait(false);
            }

            var session = new SmtpSession(stream);
            try
            {
                await session.ExpectAsync(220, token).ConfigureAwait(false);
                var capabilities = await session.EhloAsync(HeloName(), token).ConfigureAwait(false);
                if (settings.Security == EmailSecurity.StartTls)
                {
                    if (!capabilities.Any(line => line.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new SmtpProtocolException(0, "服务器不支持 STARTTLS");
                    }

                    await session.CommandAsync("STARTTLS", 220, token).ConfigureAwait(false);
                    await session.DisposeAsync().ConfigureAwait(false);
                    stream = await UpgradeAsync(stream, settings.Host, token).ConfigureAwait(false);
                    session = new SmtpSession(stream);
                    capabilities = await session.EhloAsync(HeloName(), token).ConfigureAwait(false);
                }

                if (settings.Username.Length > 0)
                {
                    await AuthenticateAsync(session, settings, capabilities, token).ConfigureAwait(false);
                }

                await session.CommandAsync($"MAIL FROM:<{settings.From}>", 250, token).ConfigureAwait(false);
                await session.CommandAsync($"RCPT TO:<{message.To}>", 250, token).ConfigureAwait(false);
                await session.CommandAsync("DATA", 354, token).ConfigureAwait(false);
                await session.WriteDataAsync(BuildMime(message, settings), token).ConfigureAwait(false);
                await session.ExpectAsync(250, token).ConfigureAwait(false);
                await session.TryQuitAsync(token).ConfigureAwait(false);
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (EmailUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                           || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "email.delivery_failed errorType={ErrorType} smtpStatus={SmtpStatus}",
                exception.GetType().Name,
                exception is SmtpProtocolException protocol ? protocol.StatusCode : 0);
            throw new EmailUnavailableException("email.delivery_failed");
        }
    }

    private static async Task AuthenticateAsync(
        SmtpSession session,
        EmailSettings settings,
        IReadOnlyList<string> capabilities,
        CancellationToken cancellationToken)
    {
        var authLine = capabilities.FirstOrDefault(line =>
            line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var mechanisms = authLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(item => item.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        if (mechanisms.Count == 0 || mechanisms.Contains("PLAIN"))
        {
            var payload = Convert.ToBase64String(
                Encoding.UTF8.GetBytes("\0" + settings.Username + "\0" + settings.Password));
            await session.CommandAsync("AUTH PLAIN " + payload, 235, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (mechanisms.Contains("LOGIN"))
        {
            await session.CommandAsync("AUTH LOGIN", 334, cancellationToken).ConfigureAwait(false);
            await session.CommandAsync(
                Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Username)),
                334,
                cancellationToken).ConfigureAwait(false);
            await session.CommandAsync(
                Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Password)),
                235,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new SmtpProtocolException(0, "服务器没有可用的 AUTH 机制");
    }

    private static Task<Stream> UpgradeAsync(Stream inner, string host, CancellationToken cancellationToken)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        return UpgradeCoreAsync(ssl, host, cancellationToken);
    }

    private static async Task<Stream> UpgradeCoreAsync(
        SslStream ssl,
        string host,
        CancellationToken cancellationToken)
    {
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
            cancellationToken).ConfigureAwait(false);
        return ssl;
    }

    private static string HeloName()
    {
        var host = Environment.MachineName;
        return host.Length == 0 ? "mosaic-server" : host;
    }

    private static string BuildMime(EmailMessage message, EmailSettings settings)
    {
        if (ContainsNewline(message.To) || ContainsNewline(settings.From) || ContainsNewline(message.Subject)
            || ContainsNewline(settings.FromName))
        {
            throw new SmtpProtocolException(0, "邮件头包含非法换行");
        }

        var boundary = "mosaic-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var builder = new StringBuilder();
        builder.Append("From: ").Append(EncodeWord(settings.FromName)).Append(" <").Append(settings.From).Append(">\r\n");
        builder.Append("To: <").Append(message.To).Append(">\r\n");
        builder.Append("Subject: ").Append(EncodeWord(message.Subject)).Append("\r\n");
        builder.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        builder.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
            .Append('@').Append(settings.Host).Append(">\r\n");
        builder.Append("MIME-Version: 1.0\r\n");
        builder.Append("Content-Type: multipart/alternative; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
        AppendPart(builder, boundary, "text/plain", message.TextBody);
        AppendPart(builder, boundary, "text/html", message.HtmlBody);
        builder.Append("--").Append(boundary).Append("--\r\n");
        return builder.ToString();
    }

    private static void AppendPart(StringBuilder builder, string boundary, string contentType, string body)
    {
        builder.Append("--").Append(boundary).Append("\r\n");
        builder.Append("Content-Type: ").Append(contentType).Append("; charset=utf-8\r\n");
        builder.Append("Content-Transfer-Encoding: base64\r\n\r\n");
        builder.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(body), Base64FormattingOptions.InsertLineBreaks));
        builder.Append("\r\n");
    }

    private static string EncodeWord(string value) =>
        "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";

    private static bool ContainsNewline(string value) =>
        value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal);

    private sealed class SmtpSession : IAsyncDisposable
    {
        private readonly Stream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public SmtpSession(Stream stream)
        {
            _stream = stream;
            _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        }

        public async Task ExpectAsync(int expected, CancellationToken cancellationToken)
        {
            var (code, text) = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            if (code != expected)
            {
                throw new SmtpProtocolException(code, text);
            }
        }

        public async Task<IReadOnlyList<string>> EhloAsync(string host, CancellationToken cancellationToken)
        {
            await WriteLineAsync("EHLO " + host, cancellationToken).ConfigureAwait(false);
            var (code, text) = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            if (code != 250)
            {
                throw new SmtpProtocolException(code, text);
            }

            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        public async Task CommandAsync(string command, int expected, CancellationToken cancellationToken)
        {
            await WriteLineAsync(command, cancellationToken).ConfigureAwait(false);
            await ExpectAsync(expected, cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteDataAsync(string body, CancellationToken cancellationToken)
        {
            var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
            var builder = new StringBuilder(normalized.Length + 16);
            foreach (var line in normalized.Split("\r\n", StringSplitOptions.None))
            {
                if (line.StartsWith(".", StringComparison.Ordinal))
                {
                    builder.Append('.');
                }

                builder.Append(line).Append("\r\n");
            }

            builder.Append(".\r\n");
            await _stream.WriteAsync(Encoding.UTF8.GetBytes(builder.ToString()), cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task TryQuitAsync(CancellationToken cancellationToken)
        {
            try
            {
                await WriteLineAsync("QUIT", cancellationToken).ConfigureAwait(false);
                await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SmtpProtocolException or OperationCanceledException)
            {
                // The message was already accepted; QUIT failures are not user-visible.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            _reader.Dispose();
        }

        private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        private async Task<(int Code, string Text)> ReadResponseAsync(CancellationToken cancellationToken)
        {
            var text = new StringBuilder();
            var code = 0;
            while (true)
            {
                var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new SmtpProtocolException(0, "连接已关闭");
                if (line.Length < 3
                    || !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    throw new SmtpProtocolException(0, "响应格式无效");
                }

                if (code == 0)
                {
                    code = parsed;
                }

                if (line.Length > 4)
                {
                    text.Append(line[4..]).Append('\n');
                }

                if (line.Length == 3 || line[3] == ' ')
                {
                    return (code, text.ToString());
                }
            }
        }
    }

    private sealed class SmtpProtocolException(int statusCode, string text) : Exception(text)
    {
        public int StatusCode { get; } = statusCode;
    }
}
