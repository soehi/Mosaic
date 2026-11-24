using Microsoft.AspNetCore.Http.Features;
using Mosaic.Server.Auth;
using Mosaic.Server.Http;

namespace Mosaic.Server.Attachments;

public static class AttachmentEndpoints
{
    public static void MapAttachmentEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Manifest + usage: the client uploads what it has locally and deletes what
        // the server still holds but the local library no longer references.
        app.MapGet("/v1/attachments", async (
            HttpContext http,
            SessionService sessions,
            AttachmentService attachments,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            var manifest = await attachments.ListAsync(user.Id, cancellationToken).ConfigureAwait(false);
            return Results.Json(new
            {
                items = manifest.Items.Select(item => new
                {
                    id = item.Id,
                    taskId = item.TaskId,
                    fileName = item.FileName,
                    sizeBytes = item.SizeBytes,
                    sha256 = item.Sha256,
                    createdAt = TimeFormat.Iso(item.CreatedAt),
                    updatedAt = TimeFormat.Iso(item.UpdatedAt),
                }).ToArray(),
                usageBytes = manifest.UsageBytes,
                quotaBytes = attachments.QuotaBytes,
                maxBytes = attachments.MaxBytes,
            });
        });

        app.MapPut("/v1/attachments/{attachmentId}", async (
            string attachmentId,
            HttpContext http,
            SessionService sessions,
            AttachmentService attachments,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);

            // A single attachment may exceed the global JSON body cap (16 MiB by default);
            // raise the limit for this request only, before the body starts streaming.
            var bodySize = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
            {
                bodySize.MaxRequestBodySize = attachments.MaxBytes;
            }

            var result = await attachments.SaveAsync(
                user.Id,
                attachmentId,
                http.Request.Query["taskId"],
                http.Request.Query["fileName"],
                http.Request.Body,
                http.Request.ContentLength,
                http.Request.Headers["X-Mosaic-Sha256"],
                cancellationToken).ConfigureAwait(false);
            return Results.Json(
                new
                {
                    id = result.Item.Id,
                    taskId = result.Item.TaskId,
                    fileName = result.Item.FileName,
                    sizeBytes = result.Item.SizeBytes,
                    sha256 = result.Item.Sha256,
                    createdAt = TimeFormat.Iso(result.Item.CreatedAt),
                    updatedAt = TimeFormat.Iso(result.Item.UpdatedAt),
                    duplicate = result.Duplicate,
                },
                statusCode: result.Duplicate ? 200 : 201);
        });

        app.MapGet("/v1/attachments/{attachmentId}", async (
            string attachmentId,
            HttpContext http,
            SessionService sessions,
            AttachmentService attachments,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            var file = await attachments.OpenAsync(user.Id, attachmentId, cancellationToken).ConfigureAwait(false)
                ?? throw new ApiException(404, "attachment.not_found", "找不到该附件。");
            return Results.File(file.Path, "application/octet-stream", file.Item.FileName);
        });

        app.MapDelete("/v1/attachments/{attachmentId}", async (
            string attachmentId,
            HttpContext http,
            SessionService sessions,
            AttachmentService attachments,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            await attachments.DeleteAsync(user.Id, attachmentId, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        });
    }
}