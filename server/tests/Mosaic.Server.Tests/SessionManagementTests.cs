using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mosaic.Server.Tests;

public sealed class SessionManagementTests
{
    [Fact]
    public async Task Sessions_can_be_listed_and_revoked()
    {
        await using var host = await TestServerHost.StartAsync();
        var (email, password, first) = await host.RegisterAsync();
        var firstToken = TestServerHost.AccessToken(first);

        var second = await TestServerHost.JsonAsync(await host.PostAsync("/v1/auth/login", new
        {
            email,
            password,
            deviceLabel = "第二台电脑",
            platform = "windows",
        }));
        var secondToken = TestServerHost.AccessToken(second);

        var list = await TestServerHost.JsonAsync(await host.GetAsync("/v1/auth/sessions", firstToken));
        var sessions = list.GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(2, sessions.Length);
        Assert.Single(sessions, item => item.GetProperty("current").GetBoolean());
        Assert.Contains(
            sessions,
            item => string.Equals(item.GetProperty("deviceLabel").GetString(), "第二台电脑", StringComparison.Ordinal)
                && string.Equals(item.GetProperty("platform").GetString(), "windows", StringComparison.Ordinal));

        var other = sessions.First(item => !item.GetProperty("current").GetBoolean());
        var otherId = other.GetProperty("id").GetString()!;
        var revoke = await host.PostAsync($"/v1/auth/sessions/{otherId}/revoke", null, firstToken);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var revoked = await host.GetAsync("/v1/auth/me", secondToken);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("auth.session_revoked", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(revoked)));

        var missing = await host.PostAsync("/v1/auth/sessions/not-a-session/revoke", null, firstToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var remaining = await TestServerHost.JsonAsync(await host.GetAsync("/v1/auth/sessions", firstToken));
        Assert.Equal(1, remaining.GetProperty("sessions").GetArrayLength());
    }

    [Fact]
    public async Task Revoke_all_can_keep_the_current_session()
    {
        await using var host = await TestServerHost.StartAsync();
        var (email, password, first) = await host.RegisterAsync();
        var firstToken = TestServerHost.AccessToken(first);
        var second = await TestServerHost.JsonAsync(
            await host.PostAsync("/v1/auth/login", new { email, password, deviceLabel = "second" }));
        var secondToken = TestServerHost.AccessToken(second);

        var keep = await host.PostAsync("/v1/auth/sessions/revoke-all", new { keepCurrent = true }, firstToken);
        Assert.Equal(HttpStatusCode.NoContent, keep.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/v1/auth/me", firstToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync("/v1/auth/me", secondToken)).StatusCode);

        var all = await host.PostAsync("/v1/auth/sessions/revoke-all", null, firstToken);
        Assert.Equal(HttpStatusCode.NoContent, all.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync("/v1/auth/me", firstToken)).StatusCode);
    }

    [Fact]
    public async Task Account_deletion_requires_reauthentication_and_revokes_everything()
    {
        await using var host = await TestServerHost.StartAsync();
        var (email, password, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);
        await host.PutAsync("/v1/backups", new
        {
            deviceId = "device-1",
            items = new[] { new { kind = "task", key = "task-1", revision = 1, payload = new { title = "x" } } },
        }, token);
        await host.PostAsync("/v1/sync/changes", new
        {
            deviceId = "device-1",
            kind = "task",
            key = "task-1",
            payload = new { title = "x" },
            deleted = false,
            baseServerRevision = 0,
            clientRevision = 1,
            idempotencyKey = "key-1",
        }, token);
        await host.GetAsync("/v1/sync/changes?cursor=0&deviceId=device-1", token);
        var deviceStart = await TestServerHost.JsonAsync(await host.PostAsync("/v1/device/start", new
        {
            deviceLabel = "phone",
            platform = "android",
        }));
        await host.PostAsync("/v1/device/approve", new
        {
            userCode = deviceStart.GetProperty("userCode").GetString(),
        }, token);

        var wrong = await host.PostAsync("/v1/auth/account/delete", new { password = "wrong password" }, token);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("auth.reauth_required", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(wrong)));

        var wrongCode = await host.PostAsync("/v1/auth/account/delete", new { code = "000000" }, token);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCode.StatusCode);
        Assert.Equal("auth.reauth_required", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(wrongCode)));

        var delete = await host.PostAsync("/v1/auth/account/delete", new { password }, token);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var me = await host.GetAsync("/v1/auth/me", token);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        // The account row (and with it every session) is gone, so the token no longer
        // resolves to a revoked session and is reported as an unknown credential.
        Assert.Equal("auth.required", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(me)));

        var login = await host.PostAsync("/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("auth.invalid_credentials", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(login)));

        Assert.Equal(0L, await CountAsync(host, "users"));
        Assert.Equal(0L, await CountAsync(host, "backups"));
        Assert.Equal(0L, await CountAsync(host, "backup_items"));
        Assert.Equal(0L, await CountAsync(host, "sessions"));
        Assert.Equal(0L, await CountAsync(host, "refresh_tokens"));
        Assert.Equal(0L, await CountAsync(host, "device_auth"));
        Assert.Equal(0L, await CountAsync(host, "sync_changes"));
        Assert.Equal(0L, await CountAsync(host, "sync_cursors"));
        Assert.Equal(0L, await CountAsync(host, "sync_items"));
        Assert.Equal(0L, await CountAsync(host, "email_codes"));
        Assert.DoesNotContain(email, string.Join('\n', await AuditUserIdsAsync(host)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Account_deletion_accepts_a_fresh_email_code()
    {
        await using var host = await TestServerHost.StartAsync();
        var (email, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var code = await host.RequestCodeAsync(email, "delete-account");
        var delete = await host.PostAsync("/v1/auth/account/delete", new { code }, token);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(0L, await CountAsync(host, "users"));
        Assert.Equal(0L, await CountAsync(host, "email_codes"));

        var mailsBefore = host.Email.CountFor(email);
        var again = await host.PostAsync("/v1/auth/email-code", new { email, purpose = "delete-account" });
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(mailsBefore, host.Email.CountFor(email));
    }

    [Fact]
    public async Task Unknown_purposes_are_rejected()
    {
        await using var host = await TestServerHost.StartAsync();
        var response = await host.PostAsync("/v1/auth/email-code", new
        {
            email = "someone@example.com",
            purpose = "admin",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("request.invalid", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(response)));
    }

    private static async Task<long> CountAsync(TestServerHost host, string table)
    {
        await using var connection = new SqliteConnection("Data Source=" + host.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<string>> AuditUserIdsAsync(TestServerHost host)
    {
        await using var connection = new SqliteConnection("Data Source=" + host.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id FROM settings_audit";
        var results = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }
}
