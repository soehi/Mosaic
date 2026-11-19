using System.Net;
using System.Text.Json;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Sync;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync.Tests;

public sealed class SyncWriteTests
{
    private static ChangePushRequestDto Request(bool deleted = false) => new()
    {
        DeviceId = "device-1",
        Kind = "task",
        Key = Ids.One,
        Payload = deleted
            ? null
            : JsonSerializer.SerializeToElement(new { title = "Mine" }, WireJson.Options),
        Deleted = deleted,
        BaseServerRevision = 3,
        ClientRevision = 4,
        IdempotencyKey = "idem-1",
    };

    private static (SyncWriteClient Client, StubHandler Handler, MosaicApiClient Api) Create(
        Func<RecordedRequest, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var api = new MosaicApiClient(new HttpClient(handler)) { BaseUrl = "https://example.test" };
        return (new SyncWriteClient(api), handler, api);
    }

    [Fact]
    public async Task Accepted_change_returns_the_stored_revision()
    {
        var (client, handler, api) = Create(_ => Responses.Json(
            HttpStatusCode.OK,
            """
            {
              "change": {
                "cursor": "c1",
                "serverRevision": 4,
                "kind": "task",
                "key": "11111111-1111-1111-1111-111111111111",
                "deleted": false,
                "payload": { "title": "Mine" },
                "deviceId": "device-1",
                "createdAt": "2026-11-19T12:00:00Z"
              },
              "replayed": false
            }
            """));
        using var _ = api;

        var result = await client.PushAsync(Request());

        Assert.Equal(SyncWriteStatus.Applied, result.Status);
        Assert.Equal(4, result.Change?.ServerRevision);
        var sent = Assert.Single(handler.Requests);
        Assert.EndsWith("/v1/sync/changes", sent.Uri, StringComparison.Ordinal);
        Assert.Contains("\"idempotencyKey\":\"idem-1\"", sent.Body);
        Assert.Contains("\"baseServerRevision\":3", sent.Body);
    }

    [Fact]
    public async Task Replayed_write_is_reported_as_replayed()
    {
        var (client, _, api) = Create(_ => Responses.Json(
            HttpStatusCode.OK,
            """{"change":{"serverRevision":4,"kind":"task","key":"k","deleted":false,"payload":{}},"replayed":true}"""));
        using var _ = api;

        var result = await client.PushAsync(Request());

        Assert.Equal(SyncWriteStatus.Replayed, result.Status);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Conflict_returns_the_server_winner()
    {
        var (client, _, api) = Create(_ => Responses.Json(
            (HttpStatusCode)409,
            """
            {
              "code": "sync.conflict",
              "details": {
                "current": {
                  "kind": "task",
                  "key": "11111111-1111-1111-1111-111111111111",
                  "serverRevision": 8,
                  "deleted": false,
                  "payload": { "title": "Theirs" }
                }
              }
            }
            """));
        using var _ = api;

        var result = await client.PushAsync(Request());

        Assert.Equal(SyncWriteStatus.Conflict, result.Status);
        Assert.Equal(8, result.Current?.ServerRevision);
        Assert.Equal(WireErrorCodes.SyncConflict, result.Error?.Code);
    }

    [Fact]
    public async Task Rejected_session_becomes_signed_out()
    {
        var (client, _, api) = Create(_ => Responses.Error(
            HttpStatusCode.Unauthorized,
            WireErrorCodes.AuthSessionRevoked));
        using var _ = api;

        var result = await client.PushAsync(Request());

        Assert.Equal(SyncWriteStatus.SignedOut, result.Status);
        Assert.Equal(SyncErrorKind.SessionExpired, result.Error?.Kind);
    }
}
