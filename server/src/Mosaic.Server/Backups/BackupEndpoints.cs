using Mosaic.Server.Auth;
using Mosaic.Server.Http;

namespace Mosaic.Server.Backups;

public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPut("/v1/backups", async (
            BackupUploadRequest body,
            HttpContext http,
            SessionService sessions,
            BackupService backups,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            var result = await backups.UploadAsync(user.Id, body, cancellationToken).ConfigureAwait(false);
            return Results.Json(
                new
                {
                    snapshotId = result.SnapshotId,
                    createdAt = TimeFormat.Iso(result.CreatedAt),
                    contentSha256 = result.ContentSha256,
                    sizeBytes = result.SizeBytes,
                    itemCount = result.ItemCount,
                    serverRevision = result.ServerRevision,
                    cursor = result.Cursor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    duplicate = result.Duplicate,
                },
                statusCode: result.Duplicate ? 200 : 201);
        });

        app.MapGet("/v1/backups/latest", async (
            HttpContext http,
            SessionService sessions,
            BackupService backups,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            var manifest = await backups.GetLatestAsync(user.Id, cancellationToken).ConfigureAwait(false)
                ?? throw NotFound();
            return Results.Json(ManifestJson(manifest));
        });

        app.MapGet("/v1/backups/{snapshotId}", async (
            string snapshotId,
            HttpContext http,
            SessionService sessions,
            BackupService backups,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            var snapshot = await backups.GetAsync(user.Id, snapshotId, cancellationToken).ConfigureAwait(false)
                ?? throw NotFound();
            return Results.Json(SnapshotJson(snapshot));
        });

        app.MapPost("/v1/backups/{snapshotId}/restore", async (
            string snapshotId,
            HttpContext http,
            SessionService sessions,
            BackupService backups,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            var snapshot = await backups.GetAsync(user.Id, snapshotId, cancellationToken).ConfigureAwait(false)
                ?? throw NotFound();
            await backups.RecordRestoreAsync(user.Id, snapshotId, cancellationToken).ConfigureAwait(false);
            return Results.Json(SnapshotJson(snapshot));
        });
    }

    private static object ManifestJson(BackupManifest manifest) => new
    {
        snapshotId = manifest.SnapshotId,
        createdAt = TimeFormat.Iso(manifest.CreatedAt),
        baseSnapshotId = manifest.BaseSnapshotId,
        contentSha256 = manifest.ContentSha256,
        sizeBytes = manifest.SizeBytes,
        itemCount = manifest.ItemCount,
        taskCount = manifest.Items.Count(item => item.Kind == "task"),
        settingCount = manifest.Items.Count(item => item.Kind == "setting"),
        serverRevision = manifest.ServerRevision,
        cursor = manifest.Cursor.ToString(System.Globalization.CultureInfo.InvariantCulture),
        items =manifest.Items.Select(item => new
        {
            kind = item.Kind,
            key = item.Key,
            revision = item.Revision,
        }).ToArray(),
    };

    private static object SnapshotJson(BackupSnapshot snapshot)
    {
        var manifest = snapshot.Manifest;
        return new
        {
            snapshotId = manifest.SnapshotId,
            createdAt = TimeFormat.Iso(manifest.CreatedAt),
            baseSnapshotId = manifest.BaseSnapshotId,
            contentSha256 = manifest.ContentSha256,
            sizeBytes = manifest.SizeBytes,
            itemCount = manifest.ItemCount,
            taskCount = manifest.Items.Count(item => item.Kind == "task"),
            settingCount = manifest.Items.Count(item => item.Kind == "setting"),
            serverRevision = manifest.ServerRevision,
            cursor = manifest.Cursor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            items = snapshot.Items.Select(item => new
            {
                kind = item.Kind,
                key = item.Key,
                revision = item.Revision,
                payload = item.Payload,
            }).ToArray(),
        };
    }

    private static ApiException NotFound() =>
        new(404, "backup.not_found", "找不到该备份。");
}
