using System.Diagnostics;
using System.Text.Json;
using Mosaic.Server.Http;
using Mosaic.Server.Pages;
using Mosaic.Server.Security;

namespace Mosaic.Server.Auth;

public sealed record DeviceStartRequest(string? DeviceLabel, string? Platform, string? AppVersion);

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
            string? platform = null;
            string? appVersion = null;
            if (AuthSupport.HasBody(http.Request))
            {
                try
                {
                    var body = await http.Request.ReadFromJsonAsync<DeviceStartRequest>(cancellationToken)
                        .ConfigureAwait(false);
                    label = body?.DeviceLabel;
                    platform = body?.Platform;
                    appVersion = body?.AppVersion;
                }
                catch (JsonException)
                {
                    throw new ApiException(400, "request.invalid", "请求格式无效。");
                }
            }

            var start = await flow
                .StartAsync(label, platform, appVersion, ipHash, cancellationToken)
                .ConfigureAwait(false);
            AuthSupport.LogEvent(logger, "device.start", "ok", ipHash, Stopwatch.GetTimestamp());
            return Results.Json(new
            {
                deviceCode = start.DeviceCode,
                userCode = start.UserCode,
                verificationUri = start.VerificationUri,
                verificationUriComplete = start.VerificationUriComplete,
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
                ? Results.Json(new { status = result.Status, interval = result.Interval })
                : Results.Json(new
                {
                    status = result.Status,
                    interval = result.Interval,
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
            AuthSupport.LogEvent(logger, "device.approve", "ok", ipHash, Stopwatch.GetTimestamp(), user.Id);
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
            AuthSupport.LogEvent(logger, "device.deny", "ok", ipHash, Stopwatch.GetTimestamp(), user.Id);
            return Results.Json(new { status = "denied" });
        });

        app.MapGet("/v1/device/info", async (
            string? userCode,
            HttpContext http,
            SessionService sessions,
            DeviceFlowService flow,
            SecretHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var ipHash = AuthSupport.IpHash(http, hasher);
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            var info = await flow.InfoAsync(user, userCode, cancellationToken).ConfigureAwait(false);
            AuthSupport.LogEvent(logger, "device.info", "ok", ipHash, Stopwatch.GetTimestamp(), user.Id);
            return Results.Json(new
            {
                deviceLabel = info.DeviceLabel,
                platform = info.Platform,
                appVersion = info.AppVersion,
                state = info.State,
                expiresAt = TimeFormat.Iso(info.ExpiresAt),
                approvedAt = info.ApprovedAt is { } approvedAt ? TimeFormat.Iso(approvedAt) : null,
            });
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
