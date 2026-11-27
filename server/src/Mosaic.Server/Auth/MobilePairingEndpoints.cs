using Mosaic.Server.Http;
using Mosaic.Server.Security;

namespace Mosaic.Server.Auth;

internal static class MobilePairingEndpoints
{
    public static void MapMobilePairing(this WebApplication app, MobileRelease? release)
    {
        var group = app.MapGroup("/v1/mobile/pairing");
        group.AddEndpointFilter(async (context, next) =>
        {
            if (release is null)
            {
                throw new ApiException(503, "mobile.unavailable", "服务器的手机组件安装包或发布清单尚未就绪。");
            }
            if (!context.HttpContext.Request.IsHttps)
            {
                throw new ApiException(503, "mobile.https_required", "手机组件配对要求 HTTPS 请求。");
            }

            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        group.MapPost("/start", async (HttpContext http, SessionService sessions, MobilePairingService flow, CancellationToken ct) =>
        {
            var principal = await AuthSupport.RequirePrincipalAsync(http, sessions);
            var reference = await flow.StartAsync(principal, ct);
            return Results.Json(new { reference, url = new Uri(release!.Origin, "/device/mobile/connect#" + reference).AbsoluteUri });
        });
        group.MapPost("/status", async (ReferenceRequest body, HttpContext http, SessionService sessions, MobilePairingService flow, CancellationToken ct) =>
            Results.Json(await flow.StatusAsync(await AuthSupport.RequirePrincipalAsync(http, sessions), body.Reference, ct)));
        group.MapPost("/approve", async (ApproveRequest body, HttpContext http, SessionService sessions, MobilePairingService flow, CancellationToken ct) =>
        {
            await flow.ApproveAsync(await AuthSupport.RequirePrincipalAsync(http, sessions), body.Reference, body.ShortCode, ct);
            return Results.NoContent();
        });
        group.MapPost("/cancel", async (ReferenceRequest body, HttpContext http, SessionService sessions, MobilePairingService flow, CancellationToken ct) =>
        {
            await flow.CancelAsync(await AuthSupport.RequirePrincipalAsync(http, sessions), body.Reference, ct);
            return Results.NoContent();
        });
        group.MapPost("/claim", async (ClaimRequest body, HttpContext http, SecretHasher hasher, MobilePairingService flow, CancellationToken ct) =>
            Results.Json(await flow.ClaimAsync(body.Reference, body.DeviceLabel, body.AppVersion, AuthSupport.IpHash(http, hasher), ct)));
        group.MapPost("/poll", async (DevicePollRequest body, HttpContext http, SecretHasher hasher, MobilePairingService flow, CancellationToken ct) =>
        {
            var result = await flow.PollAsync(body.DeviceCode, AuthSupport.IpHash(http, hasher), ct);
            return Results.Json(new
            {
                result.Status,
                result.Interval,
                session = result.Session is { } session ? AuthSupport.SessionJson(session) : null,
            });
        });

        if (release is null)
        {
            return;
        }

        app.MapGet("/device/mobile", (HttpContext http) => http.Request.IsHttps
            ? Results.Content(LandingPage, "text/html; charset=utf-8") : Results.NotFound());
        app.MapGet("/device/mobile/connect", (HttpContext http) => http.Request.IsHttps
            ? Results.Content(LandingPage, "text/html; charset=utf-8") : Results.NotFound());
        app.MapGet("/device/mobile.apk", (HttpContext http) => http.Request.IsHttps
            ? Results.File(release.ApkPath, "application/vnd.android.package-archive", "mosaic-mobile.apk", enableRangeProcessing: true)
            : Results.NotFound());
        app.MapGet("/.well-known/assetlinks.json", (HttpContext http) => http.Request.IsHttps
            ? Results.Json(new[]
            {
                new
                {
                    relation = new[] { "delegate_permission/common.handle_all_urls" },
                    target = new
                    {
                        @namespace = "android_app",
                        package_name = MobileRelease.PackageName,
                        sha256_cert_fingerprints = new[] { release.CertificateSha256 },
                    },
                },
            }) : Results.NotFound());
    }

    private sealed record ReferenceRequest(string? Reference);
    private sealed record ApproveRequest(string? Reference, string? ShortCode);
    private sealed record ClaimRequest(string? Reference, string? DeviceLabel, string? AppVersion);

    private const string LandingPage = """
        <!doctype html>
        <html lang="zh-CN">
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>下载 Mosaic 手机组件</title>
        <style>
          body{font:16px/1.6 system-ui,sans-serif;background:#fff8ee;color:#2b201c;margin:0;padding:24px}
          main{max-width:28rem;margin:8vh auto;padding:32px;border-radius:24px;background:linear-gradient(#ffca57,#f4a06d)}
          h1{font-size:1.65rem;line-height:1.2;margin:0 0 1rem} a{color:#332017}
        </style>
        <main>
          <h1>Mosaic 手机组件</h1>
          <p>安装 APK 后，点「打开组件」或重新扫描桌面二维码。核对手机与桌面的配对码，再在桌面批准。</p>
          <p><a href="/device/mobile.apk">下载 Android APK</a></p>
          <p><a id="connect" hidden>打开组件</a></p>
        </main>
        <script>
          const ref = location.hash.slice(1);
          if (/^[A-Za-z0-9_-]{43}$/.test(ref)) {
            const link = document.querySelector('#connect');
            link.href = '/device/mobile/connect#' + ref;
            link.hidden = false;
          }
        </script>
        </html>
        """;
}
