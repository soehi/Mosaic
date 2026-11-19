using System.Globalization;
using Mosaic.Server.Auth;
using Mosaic.Server.Http;

namespace Mosaic.Server.Sync;

public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/v1/sync/changes", async (
            SyncChangeRequest body,
            HttpContext http,
            SessionService sessions,
            SyncService sync,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            var result = await sync.PushAsync(user.Id, body, cancellationToken).ConfigureAwait(false);
            return Results.Json(new
            {
                change = ChangeJson(result.Change),
                replayed = result.Replayed,
            });
        });

        app.MapGet("/v1/sync/changes", async (
            HttpContext http,
            SessionService sessions,
            SyncService sync,
            string? cursor,
            string? limit,
            string? deviceId,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            var parsedCursor = 0L;
            if (!string.IsNullOrWhiteSpace(cursor)
                && (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out parsedCursor)
                    || parsedCursor < 0))
            {
                throw new ApiException(400, "sync.invalid", "cursor 无效。");
            }

            var parsedLimit = 200;
            if (!string.IsNullOrWhiteSpace(limit)
                && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out parsedLimit)
                    || parsedLimit is < 1 or > 500))
            {
                throw new ApiException(400, "sync.invalid", "limit 必须在 1 到 500 之间。");
            }

            var result = await sync.PullAsync(user.Id, parsedCursor, parsedLimit, deviceId, cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new
            {
                changes = result.Changes.Select(ChangeJson).ToArray(),
                nextCursor = SyncService.FormatCursor(result.NextCursor),
                hasMore = result.HasMore,
            });
        });
    }

    private static object ChangeJson(SyncChange change) => new
    {
        cursor = SyncService.FormatCursor(change.Seq),
        serverRevision = change.ServerRevision,
        kind = change.Kind,
        key = change.Key,
        deleted = change.Deleted,
        payload = change.Payload,
        deviceId = change.DeviceId,
        createdAt = TimeFormat.Iso(change.CreatedAt),
    };
}
