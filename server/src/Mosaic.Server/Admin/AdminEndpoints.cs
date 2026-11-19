using System.Security.Cryptography;
using System.Text;
using Mosaic.Server.Emailing;
using Mosaic.Server.Http;
using Mosaic.Server.Security;

namespace Mosaic.Server.Admin;

public sealed record MailConfigRequest(
    string? Host,
    int? Port,
    string? Security,
    string? Username,
    string? Password,
    string? From,
    string? FromName);

public sealed record MailTestRequest(string? Email);

/// <summary>
/// Deployment-time mail configuration. The endpoint only exists when
/// MOSAIC_ADMIN_TOKEN is set; the password is encrypted at rest and reads only
/// report whether one is stored.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/v1/admin/mail", async (
            HttpContext http,
            ServerOptions options,
            EmailSettingsStore store,
            CancellationToken cancellationToken) =>
        {
            RequireAdmin(http, options);
            return Results.Json(await store.GetViewAsync(cancellationToken).ConfigureAwait(false));
        });

        app.MapPut("/v1/admin/mail", async (
            MailConfigRequest body,
            HttpContext http,
            ServerOptions options,
            EmailSettingsStore store,
            CancellationToken cancellationToken) =>
        {
            RequireAdmin(http, options);
            var current = await store.GetAsync(cancellationToken).ConfigureAwait(false);
            var host = (body.Host ?? current.Host).Trim();
            var port = body.Port ?? current.Port;
            if (port is < 1 or > 65535)
            {
                throw new ApiException(422, "request.invalid", "端口必须在 1 到 65535 之间。");
            }

            EmailSecurity security;
            try
            {
                security = body.Security is null
                    ? current.Security
                    : EmailSettings.ParseSecurity(body.Security);
            }
            catch (InvalidOperationException)
            {
                throw new ApiException(422, "request.invalid", "security 只能是 starttls 或 ssl。");
            }

            var from = (body.From ?? current.From).Trim();
            if (host.Length > 0 && !EmailNormalizer.IsValid(from))
            {
                throw new ApiException(422, "request.invalid", "发件人邮箱无效。");
            }

            var fromName = (body.FromName ?? current.FromName).Trim();
            if (fromName.Length > 80 || fromName.Any(char.IsControl))
            {
                throw new ApiException(422, "request.invalid", "发件人名称无效。");
            }

            var settings = new EmailSettings
            {
                Host = host,
                Port = port,
                Security = security,
                Username = (body.Username ?? current.Username).Trim(),
                Password = body.Password ?? current.Password,
                From = from,
                FromName = fromName.Length == 0 ? "Mosaic" : fromName,
            };
            await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            return Results.Json(await store.GetViewAsync(cancellationToken).ConfigureAwait(false));
        });

        app.MapPost("/v1/admin/mail/test", async (
            MailTestRequest body,
            HttpContext http,
            ServerOptions options,
            EmailSettingsStore store,
            IEmailSender sender,
            CancellationToken cancellationToken) =>
        {
            RequireAdmin(http, options);
            var email = EmailNormalizer.Normalize(body.Email);
            var settings = await store.GetAsync(cancellationToken).ConfigureAwait(false);
            if (!settings.IsConfigured)
            {
                throw new ApiException(503, "email.not_configured", "尚未配置邮件发送。");
            }

            await sender.SendAsync(
                EmailTemplates.Test(options.ServerName) with { To = email },
                settings,
                cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    private static void RequireAdmin(HttpContext http, ServerOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AdminToken))
        {
            throw new ApiException(404, "server.not_found", "接口不存在。");
        }

        var provided = http.Request.Headers["X-Admin-Token"].ToString();
        if (provided.Length == 0 || !FixedTimeEquals(provided, options.AdminToken))
        {
            throw new ApiException(401, "admin.forbidden", "管理员令牌无效。");
        }
    }

    private static bool FixedTimeEquals(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
}
