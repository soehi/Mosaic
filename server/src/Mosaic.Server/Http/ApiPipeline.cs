using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Server.Emailing;

namespace Mosaic.Server.Http;

public static class ApiPipeline
{
    public static void UseMosaicPipeline(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var path = context.Request.Path;
            if (path.StartsWithSegments("/v1") || path.StartsWithSegments("/device"))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
            }

            if (path.StartsWithSegments("/device"))
            {
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; "
                    + "connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'self'; "
                    + "frame-ancestors 'none'";
                context.Response.Headers["X-Frame-Options"] = "DENY";
            }

            await next(context).ConfigureAwait(false);
        });

        app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (ApiException exception)
            {
                await WriteErrorAsync(
                    context,
                    exception.Status,
                    exception.Code,
                    exception.Message,
                    exception.Details,
                    exception.RetryAfterSeconds).ConfigureAwait(false);
            }
            catch (EmailUnavailableException exception)
            {
                await WriteErrorAsync(
                    context,
                    503,
                    exception.Code,
                    "邮件发送暂时不可用，请稍后重试。").ConfigureAwait(false);
            }
            catch (BadHttpRequestException)
            {
                await WriteErrorAsync(context, 400, "request.invalid", "请求格式无效。").ConfigureAwait(false);
            }
            catch (JsonException)
            {
                await WriteErrorAsync(context, 400, "request.invalid", "请求格式无效。").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The client disconnected; nothing to write.
            }
            catch (Exception exception)
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Mosaic.Server.Unhandled");
                logger.LogError(
                    "request.unhandled traceId={TraceId} path={Path} errorType={ErrorType}",
                    context.TraceIdentifier,
                    context.Request.Path.Value,
                    exception.GetType().Name);
                await WriteErrorAsync(context, 500, "server.error", "服务器内部错误，请稍后重试。")
                    .ConfigureAwait(false);
            }
        });
    }

    public static async Task WriteErrorAsync(
        HttpContext context,
        int status,
        string code,
        string message,
        object? details = null,
        int? retryAfterSeconds = null)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        // Clear() drops headers set by the outer middleware; restore the baseline.
        context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (context.Request.Path.StartsWithSegments("/v1") || context.Request.Path.StartsWithSegments("/device"))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
        }

        if (retryAfterSeconds is > 0)
        {
            context.Response.Headers.RetryAfter =
                retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }

        var payload = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
            ["traceId"] = context.TraceIdentifier,
        };
        if (details is not null)
        {
            var node = JsonSerializer.SerializeToNode(details);
            if (node is JsonObject extra)
            {
                foreach (var pair in extra)
                {
                    payload[pair.Key] = pair.Value?.DeepClone();
                }
            }
        }

        await context.Response.WriteAsJsonAsync(payload).ConfigureAwait(false);
    }
}

public static class TimeFormat
{
    public static string Iso(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
