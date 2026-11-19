using System.Diagnostics;
using System.Text.Json;
using Mosaic.Server.Http;
using Mosaic.Server.Pages;
using Mosaic.Server.Security;

namespace Mosaic.Server.Auth;

public sealed record DeviceStartRequest(string? DeviceLabel);

public sealed record DevicePollRequest(string? DeviceCode);

public sealed record DeviceCodeRequest(string? UserCode);

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mosaic.Server.Device");

        app.MapPost("/v1/device/start", async (
            HttpContext http,
            DeviceFlowService flow,
            SecretHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var ipHash = AuthSupport.IpHash(http, hasher);
            string? label = null;
            if (http.Request.ContentLength is > 0)
            {
                try
                {
                    var body = await http.Request.ReadFromJsonAsync<DeviceStartRequest>(cancellationToken)
                        .ConfigureAwait(false);
                    label = body?.DeviceLabel;
                }
                catch (JsonException)
                {
                    throw new ApiException(400, "request.invalid", "请求格式无效。");
                }
            }

            var start = await flow.StartAsync(label, ipHash, cancellationToken).ConfigureAwait(false);
            AuthSupport.LogEvent(logger, "device.start", "ok", ipHash, Stopwatch.GetTimestamp());
            return Results.Json(new
            {
                deviceCode = start.DeviceCode,
                userCode = start.UserCode,
                verificationUri = start.VerificationUri,
                expiresIn = start.ExpiresIn,
                interval = start.Interval,
            });
        });

        app.MapPost("/v1/device/poll", async (
            DevicePollRequest body,
            DeviceFlowService flow,
            CancellationToken cancellationToken) =>
        {
            var result = await flow.PollAsync(body.DeviceCode, cancellationToken).ConfigureAwait(false);
            return result.Session is null
                ? Results.Json(new { status = result.Status })
                : Results.Json(new
                {
                    status = result.Status,
                    session = AuthSupport.SessionJson(result.Session),
                });
        });

        app.MapPost("/v1/device/approve", async (
            DeviceCodeRequest body,
            HttpContext http,
            SessionService sessions,
            DeviceFlowService flow,
            SecretHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var ipHash = AuthSupport.IpHash(http, hasher);
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            await flow.ApproveAsync(user, body.UserCode, cancellationToken).ConfigureAwait(false);
            AuthSupport.LogEvent(logger, "device.approve", "ok", ipHash, Environment.TickCount64, user.Id);
            return Results.Json(new { status = "approved" });
        });

        app.MapPost("/v1/device/deny", async (
            DeviceCodeRequest body,
            HttpContext http,
            SessionService sessions,
            DeviceFlowService flow,
            SecretHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var ipHash = AuthSupport.IpHash(http, hasher);
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            await flow.DenyAsync(user, body.UserCode, cancellationToken).ConfigureAwait(false);
            AuthSupport.LogEvent(logger, "device.deny", "ok", ipHash, Environment.TickCount64, user.Id);
            return Results.Json(new { status = "denied" });
        });

        app.MapGet("/device", () => Results.Content(DevicePage.Html, "text/html; charset=utf-8"));

        app.MapGet("/", (ServerOptions options) => Results.Json(new
        {
            name = options.ServerName,
            service = "mosaic-server",
            health = "/health",
        }));
    }
}
