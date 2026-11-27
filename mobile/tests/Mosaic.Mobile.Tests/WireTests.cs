using System.Text.Json;
using Mosaic.Mobile.Core.Sync;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Tests;

public sealed class WireTests
{
    [Fact]
    public void Error_envelope_prefers_details_and_falls_back_to_root()
    {
        const string nested = """{"code":"sync.conflict","message":"x","traceId":"t","details":{"current":{"key":"k"}}}""";
        var error = WireError.TryParse(nested);
        Assert.NotNull(error);
        Assert.Equal("sync.conflict", error!.Code);
        Assert.NotNull(error.DetailsAs<SyncConflictDetailsDto>()?.Current);
        Assert.Equal("k", error.DetailsAs<SyncConflictDetailsDto>()!.Current!.Key);

        const string flattened = """{"code":"sync.conflict","current":{"key":"root"}}""";
        var flat = WireError.TryParse(flattened);
        Assert.NotNull(flat);
        Assert.Equal("root", flat!.DetailsAs<SyncConflictDetailsDto>()!.Current!.Key);

        Assert.Null(WireError.TryParse("not json"));
        Assert.Null(WireError.TryParse("""{"message":"no code"}"""));
    }

    [Fact]
    public void Change_push_request_serializes_camel_case_and_keeps_payload()
    {
        var payload = Sample.Payload(Sample.TaskId);
        var request = new ChangePushRequestDto
        {
            DeviceId = "device-1",
            Key = Sample.TaskId.ToString("D"),
            Payload = JsonDocument.Parse(payload).RootElement.Clone(),
            BaseServerRevision = 0,
            ClientRevision = 3,
            IdempotencyKey = "abc",
        };

        var json = WireJson.Serialize(request);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("device-1", root.GetProperty("deviceId").GetString());
        Assert.Equal("task", root.GetProperty("kind").GetString());
        Assert.False(root.GetProperty("deleted").GetBoolean());
        Assert.Equal(0, root.GetProperty("baseServerRevision").GetInt64());
        Assert.Equal(3, root.GetProperty("clientRevision").GetInt64());
        Assert.Equal("abc", root.GetProperty("idempotencyKey").GetString());
        Assert.Equal(Sample.TaskId.ToString("D"), root.GetProperty("payload").GetProperty("id").GetString());
    }

    [Fact]
    public void Session_and_manifest_read_camel_case()
    {
        var session = WireJson.Deserialize<SessionDto>(
            """{"accessToken":"a","refreshToken":"r","accessTokenExpiresAt":"2026-11-26T10:00:00Z","user":{"id":"u1"}}""");
        Assert.NotNull(session);
        Assert.Equal("a", session!.AccessToken);
        Assert.Equal("u1", session.User!.Id);
        Assert.Equal(DateTimeOffset.Parse("2026-11-26T10:00:00Z"), session.AccessTokenExpiresAt);

        var manifest = WireJson.Deserialize<BackupManifestDto>(
            """{"snapshotId":"s","itemCount":2,"serverRevision":9,"cursor":"7","contentSha256":"ABCD"}""");
        Assert.NotNull(manifest);
        Assert.Equal("s", manifest!.SnapshotId);
        Assert.Equal(9, manifest.ServerRevision);
        Assert.Equal("7", manifest.Cursor);
    }

    [Fact]
    public void Canonicalizer_matches_server_digest_rules()
    {
        var payload = """
            {"title":"A","notes":"","isCompleted":false,"createdAtUtc":"2026-11-26T00:00:00Z",
             "updatedAtUtc":"2026-11-26T00:00:00Z","revision":3,"extra":1.50,"中文":"值",
             "id":"11111111-1111-1111-1111-111111111111"}
            """;
        var items = new[]
        {
            Sample.BackupItem(Sample.TaskId, payload, revision: 3),
        };

        Assert.Equal(
            "b51a6bd78ece5498436a25e30851c9f2c754d0d3cacd66fdfdcf76c87b142a2c",
            BackupCanonicalizer.ComputeSha256Hex(items));
    }

    [Fact]
    public void Canonicalizer_sorts_items_by_kind_then_key()
    {
        var first = Sample.BackupItem(Sample.TaskId, Sample.Payload(Sample.TaskId));
        var second = Sample.BackupItem(Guid.Parse("22222222-2222-2222-2222-222222222222"), Sample.Payload(Guid.Parse("22222222-2222-2222-2222-222222222222")));
        Assert.Equal(
            BackupCanonicalizer.ComputeSha256Hex([first, second]),
            BackupCanonicalizer.ComputeSha256Hex([second, first]));
    }
}