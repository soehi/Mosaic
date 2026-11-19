using System.Net;
using Microsoft.Data.Sqlite;

namespace Mosaic.Server.Tests;

public sealed class AdminHealthTests
{
    [Fact]
    public async Task Health_returns_ok_and_a_version()
    {
        await using var host = await TestServerHost.StartAsync();
        var response = await host.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await TestServerHost.JsonAsync(response);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task Policy_reports_email_availability()
    {
        await using var host = await TestServerHost.StartAsync();
        var policy = await TestServerHost.JsonAsync(await host.GetAsync("/v1/auth/policy"));
        Assert.True(policy.GetProperty("registrationOpen").GetBoolean());
        Assert.True(policy.GetProperty("emailAvailable").GetBoolean());
        Assert.Equal(6, policy.GetProperty("codeLength").GetInt32());
        Assert.Equal(600, policy.GetProperty("codeTtlSeconds").GetInt32());
    }

    [Fact]
    public async Task Admin_endpoints_are_hidden_when_no_token_is_configured()
    {
        await using var host = await TestServerHost.StartAsync();
        var response = await host.GetAsync("/v1/admin/mail");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_endpoints_require_the_configured_token()
    {
        await using var host = await TestServerHost.StartAsync(options => options.AdminToken = "test-admin-token");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await host.GetAsync("/v1/admin/mail")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await host.SendAsync(HttpMethod.Get, "/v1/admin/mail", adminToken: "wrong")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await host.SendAsync(HttpMethod.Get, "/v1/admin/mail", adminToken: "test-admin-token")).StatusCode);
    }

    [Fact]
    public async Task Mail_config_is_encrypted_at_rest_and_never_returned()
    {
        const string adminToken = "test-admin-token";
        const string smtpPassword = "sup3r-secret-smtp";
        await using var host = await TestServerHost.StartAsync(options => options.AdminToken = adminToken);

        var put = await host.SendAsync(HttpMethod.Put, "/v1/admin/mail", new
        {
            host = "smtp.example.com",
            port = 587,
            security = "starttls",
            username = "mailer",
            password = smtpPassword,
            from = "noreply@example.com",
            fromName = "Mosaic Mail",
        }, adminToken: adminToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var view = await TestServerHost.JsonAsync(put);
        Assert.True(view.GetProperty("hasPassword").GetBoolean());
        Assert.True(view.GetProperty("configured").GetBoolean());
        Assert.False(view.TryGetProperty("password", out _));

        var get = await host.SendAsync(HttpMethod.Get, "/v1/admin/mail", adminToken: adminToken);
        var readBack = await ServerContent(get);
        Assert.DoesNotContain(smtpPassword, readBack, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordCipher", readBack, StringComparison.Ordinal);

        await using var connection = new SqliteConnection("Data Source=" + host.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM server_settings WHERE key = 'smtp'";
        var stored = (string)(await command.ExecuteScalarAsync())!;
        Assert.DoesNotContain(smtpPassword, stored, StringComparison.Ordinal);
        Assert.Contains("passwordCipher", stored, StringComparison.Ordinal);

        var test = await host.SendAsync(
            HttpMethod.Post,
            "/v1/admin/mail/test",
            new { email = "admin@example.com" },
            adminToken: adminToken);
        Assert.Equal(HttpStatusCode.NoContent, test.StatusCode);
        Assert.Equal(1, host.Email.CountFor("admin@example.com"));
    }

    [Fact]
    public async Task Admin_mail_settings_validate_input()
    {
        const string adminToken = "test-admin-token";
        await using var host = await TestServerHost.StartAsync(options => options.AdminToken = adminToken);

        var badPort = await host.SendAsync(HttpMethod.Put, "/v1/admin/mail", new
        {
            host = "smtp.example.com",
            port = 70000,
            from = "noreply@example.com",
        }, adminToken: adminToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badPort.StatusCode);

        var badSecurity = await host.SendAsync(HttpMethod.Put, "/v1/admin/mail", new
        {
            host = "smtp.example.com",
            security = "plain",
            from = "noreply@example.com",
        }, adminToken: adminToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badSecurity.StatusCode);

        var badFrom = await host.SendAsync(HttpMethod.Put, "/v1/admin/mail", new
        {
            host = "smtp.example.com",
            from = "not-an-address",
        }, adminToken: adminToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badFrom.StatusCode);
    }

    private static async Task<string> ServerContent(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync();
}
