using System.Text.Json;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Wire.Tests;

public sealed class WireContractTests
{
    private const string PolicySample = """
        {
          "serverName": "Mosaic",
          "registrationOpen": true,
          "requireEmailVerification": true,
          "emailAvailable": false,
          "codeLength": 6,
          "codeTtlSeconds": 600,
          "resendAfterSeconds": 60,
          "passwordMinLength": 10,
          "passwordMaxLength": 256,
          "futureField": "ignored"
        }
        """;

    private const string SessionSample = """
        {
          "accessToken": "access-token-value",
          "refreshToken": "refresh-token-value",
          "accessTokenExpiresAt": "2026-11-19T12:00:00Z",
          "refreshTokenExpiresAt": "2026-12-19T12:00:00Z",
          "user": {
            "id": "user-1",
            "email": "someone@example.com",
            "createdAt": "2026-11-01T08:00:00Z",
            "emailVerifiedAt": "2026-11-01T08:05:00Z",
            "displayName": "ignored"
          },
          "unexpected": 42
        }
        """;

    private const string ServerChangeSample = """
        {
          "seq": 1,
          "serverRevision": 7,
          "kind": "task",
          "key": "6b8b8e8e-3a37-4a33-9f3d-0e0f14b4c1a1",
          "deleted": false,
          "payload": {
            "id": "6b8b8e8e-3a37-4a33-9f3d-0e0f14b4c1a1",
            "title": "Write the report",
            "notes": "",
            "isCompleted": false,
            "createdAtUtc": "2026-11-19T09:00:00Z",
            "updatedAtUtc": "2026-11-19T09:00:00Z",
            "revision": 3,
            "archivedAtUtc": null
          },
          "deviceId": "device-1",
          "createdAt": "2026-11-19T09:00:00Z",
          "cursor": "opaque-1"
        }
        """;

    private const string FrozenChangeSample = """
        {
          "serverRevision": 9,
          "kind": "setting",
          "key": "ui.language",
          "operation": "upsert",
          "payload": { "value": "English" },
          "updatedAtUtc": "2026-11-19T10:00:00Z",
          "deviceId": "device-2"
        }
        """;

    [Fact]
    public void Policy_sample_parses_and_ignores_unknown_members()
    {
        var policy = WireJson.Deserialize<AuthPolicyDto>(PolicySample);

        Assert.NotNull(policy);
        Assert.Equal("Mosaic", policy.ServerName);
        Assert.True(policy.RegistrationOpen);
        Assert.False(policy.EmailAvailable);
        Assert.Equal(6, policy.CodeLength);
        Assert.Equal(10, policy.PasswordMinLength);
    }

    [Fact]
    public void Session_sample_parses_tokens_and_user()
    {
        var session = WireJson.Deserialize<SessionDto>(SessionSample);

        Assert.NotNull(session);
        Assert.Equal("access-token-value", session.AccessToken);
        Assert.Equal("refresh-token-value", session.RefreshToken);
        Assert.Equal("someone@example.com", session.User?.Email);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T12:00:00Z"), session.AccessTokenExpiresAt);
    }

    [Fact]
    public void Error_envelope_parses_trace_and_details()
    {
        const string body = """
            {
              "code": "auth.invalid_credentials",
              "message": "邮箱或密码不正确。",
              "traceId": "trace-1",
              "details": { "requiresSnapshot": true },
              "unknown": true
            }
            """;

        var error = WireErrorDto.TryParse(body);

        Assert.NotNull(error);
        Assert.Equal("auth.invalid_credentials", error.Code);
        Assert.Equal("trace-1", error.TraceId);
        Assert.True(error.RequiresSnapshot);
    }

    [Fact]
    public void Invalid_error_body_returns_null()
    {
        Assert.Null(WireErrorDto.TryParse("<html>bad gateway</html>"));
        Assert.Null(WireErrorDto.TryParse(null));
        Assert.Null(WireErrorDto.TryParse("{}"));
    }

    [Fact]
    public void Server_change_shape_reads_deleted_and_createdAt_aliases()
    {
        var change = WireJson.Deserialize<ChangeDto>(ServerChangeSample);

        Assert.NotNull(change);
        Assert.Equal(7, change.ServerRevision);
        Assert.Equal(ChangeKind.Task, change.ResolvedKind);
        Assert.Equal(ChangeOperation.Upsert, change.ResolvedOperation);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T09:00:00Z"), change.EffectiveTimestamp);
        Assert.Equal("opaque-1", change.Cursor);
        Assert.True(change.Payload?.TryGetProperty("title", out _));
    }

    [Fact]
    public void Frozen_change_shape_reads_operation_and_updatedAtUtc()
    {
        var change = WireJson.Deserialize<ChangeDto>(FrozenChangeSample);

        Assert.NotNull(change);
        Assert.Equal(ChangeKind.Setting, change.ResolvedKind);
        Assert.Equal(ChangeOperation.Upsert, change.ResolvedOperation);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:00Z"), change.EffectiveTimestamp);
    }

    [Fact]
    public void Delete_operation_and_deleted_boolean_agree()
    {
        var frozen = WireJson.Deserialize<ChangeDto>("""{"serverRevision":1,"kind":"task","key":"x","operation":"delete"}""");
        var alias = WireJson.Deserialize<ChangeDto>("""{"serverRevision":1,"kind":"task","key":"x","deleted":true}""");
        var payloadOnly = WireJson.Deserialize<ChangeDto>("""{"serverRevision":1,"kind":"task","key":"x","payload":{}}""");

        Assert.Equal(ChangeOperation.Delete, frozen!.ResolvedOperation);
        Assert.Equal(ChangeOperation.Delete, alias!.ResolvedOperation);
        Assert.Equal(ChangeOperation.Upsert, payloadOnly!.ResolvedOperation);
    }

    [Fact]
    public void Unknown_kind_and_operation_are_tolerated()
    {
        var change = WireJson.Deserialize<ChangeDto>(
            """{"serverRevision":1,"kind":"attachment","key":"a","operation":"merge","payload":{}}""");

        Assert.NotNull(change);
        Assert.Equal(ChangeKind.Unknown, change.ResolvedKind);
        Assert.Equal(ChangeOperation.Unknown, change.ResolvedOperation);
    }

    [Fact]
    public void Sync_changes_response_parses_and_next_cursor_is_opaque()
    {
        var json = $$"""
            {
              "changes": [{{ServerChangeSample}}],
              "nextCursor": "eyJzZXEiOjF9",
              "hasMore": true,
              "unknown": []
            }
            """;

        var response = WireJson.Deserialize<SyncChangesResponseDto>(json);

        Assert.NotNull(response);
        Assert.Single(response.Changes);
        Assert.Equal("eyJzZXEiOjF9", response.NextCursor);
        Assert.True(response.HasMore);
    }

    [Fact]
    public void Task_payload_parses_archived_timestamps()
    {
        const string payload = """
            {
              "id": "6b8b8e8e-3a37-4a33-9f3d-0e0f14b4c1a1",
              "title": "Archived",
              "notes": "note",
              "isCompleted": true,
              "createdAtUtc": "2026-11-19T09:00:00Z",
              "updatedAtUtc": "2026-11-19T10:00:00Z",
              "revision": 4,
              "completedAtUtc": "2026-11-19T09:30:00Z",
              "archivedAtUtc": "2026-11-19T10:00:00Z",
              "restoredAtUtc": null
            }
            """;

        var task = WireJson.Deserialize<TaskItemDto>(payload);

        Assert.NotNull(task);
        Assert.Equal(Guid.Parse("6b8b8e8e-3a37-4a33-9f3d-0e0f14b4c1a1"), task.Id);
        Assert.True(task.IsCompleted);
        Assert.NotNull(task.CompletedAtUtc);
        Assert.NotNull(task.ArchivedAtUtc);
        Assert.Null(task.RestoredAtUtc);
    }

    [Fact]
    public void Device_poll_statuses_are_tolerated_unknown_included()
    {
        var approved = WireJson.Deserialize<DevicePollResponseDto>(
            $$"""{"status":"approved","session":{{SessionSample}}}""");
        var unknown = WireJson.Deserialize<DevicePollResponseDto>("""{"status":"brand_new_status"}""");

        Assert.Equal(DeviceFlowState.Approved, DeviceFlowStateParser.Parse(approved?.Status));
        Assert.NotNull(approved?.Session);
        Assert.Equal(DeviceFlowState.Unknown, DeviceFlowStateParser.Parse(unknown?.Status));
        Assert.Equal("brand_new_status", unknown?.Status);
    }

    [Fact]
    public void Session_list_accepts_bare_array_and_envelope()
    {
        const string item = """
            {"id":"s1","deviceLabel":"Pixel","platform":"android","createdAt":"2026-11-19T09:00:00Z","lastSeenAt":null,"expiresAt":null,"current":true}
            """;
        var array = JsonDocument.Parse($"[{item}]").RootElement;
        var envelope = JsonDocument.Parse($$"""{"sessions":[{{item}}]}""").RootElement;

        var fromArray = SessionListParser.Parse(array);
        var fromEnvelope = SessionListParser.Parse(envelope);

        Assert.Single(fromArray);
        Assert.Single(fromEnvelope);
        Assert.True(fromArray[0].Current);
        Assert.Equal("Pixel", fromEnvelope[0].DeviceLabel);
    }

    [Fact]
    public void Request_serialization_is_camel_case_and_omits_nulls()
    {
        var json = WireJson.Serialize(new LoginRequestDto
        {
            Email = "a@b.co",
            Password = "secret",
            DeviceLabel = null,
        });

        Assert.Contains("\"email\":\"a@b.co\"", json);
        Assert.Contains("\"password\":\"secret\"", json);
        Assert.DoesNotContain("deviceLabel", json);
        Assert.DoesNotContain("DeviceLabel", json);
    }

    [Fact]
    public void Backup_snapshot_parses_items_and_optional_revision()
    {
        const string json = """
            {
              "snapshotId": "snap-1",
              "createdAt": "2026-11-19T10:00:00Z",
              "sizeBytes": 10,
              "itemCount": 1,
              "items": [
                { "kind": "task", "key": "6b8b8e8e-3a37-4a33-9f3d-0e0f14b4c1a1", "revision": 3, "payload": { "title": "x" } }
              ]
            }
            """;

        var snapshot = WireJson.Deserialize<BackupSnapshotDto>(json);

        Assert.NotNull(snapshot);
        Assert.Equal("snap-1", snapshot.SnapshotId);
        Assert.Null(snapshot.Cursor);
        Assert.Single(snapshot.Items);
        Assert.Equal("6b8b8e8e-3a37-4a33-9f3d-0e0f14b4c1a1", snapshot.Items[0].Key);
    }
}
