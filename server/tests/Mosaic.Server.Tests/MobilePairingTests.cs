using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mosaic.Server.Tests;

public sealed class MobilePairingTests
{
    [Fact]
    public async Task Pairing_requires_the_original_desktop_family_then_issues_a_session_once()
    {
        await using var host = await TestServerHost.StartAsync(ConfigureRelease, https: true);
        var (email, password, desktop) = await host.RegisterAsync();
        var (_, _, anotherUser) = await host.RegisterAsync();
        var anotherDevice = await TestServerHost.JsonAsync(await host.PostAsync("/v1/auth/login", new { email, password }));
        var access = TestServerHost.AccessToken(desktop);
        var tasks = Enumerable.Range(1, 5).Select(index => Guid.NewGuid().ToString("D")).ToArray();
        using var upload = await host.PutAsync("/v1/backups", new
        {
            deviceId = "desktop-test",
            items = tasks.Take(4).Select((key, index) => new
            {
                kind = "task", key, revision = 1, payload = new { title = $"验收任务 {index + 1}" },
            }),
        }, access);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var backup = await TestServerHost.JsonAsync(upload);
        using var push = await host.PostAsync("/v1/sync/changes", new
        {
            deviceId = "desktop-test", kind = "task", key = tasks[4], payload = new { title = "增量任务" },
            baseServerRevision = 0, clientRevision = 1, idempotencyKey = "pairing-sync-test",
        }, access);
        Assert.Equal(HttpStatusCode.OK, push.StatusCode);
        var reference = await StartAsync(host, access);
        using var landing = await host.GetAsync("/device/mobile/connect");
        using var apk = await host.GetAsync("/device/mobile.apk");
        using var association = await host.GetAsync("/.well-known/assetlinks.json");
        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
        Assert.Equal("application/vnd.android.package-archive", apk.Content.Headers.ContentType?.MediaType);
        Assert.Contains("app.mosaic.tasks", await association.Content.ReadAsStringAsync());
        var waiting = await TestServerHost.JsonAsync(await host.PostAsync(Pair("status"), new { reference }, access));
        Assert.Equal("waiting", waiting.GetProperty("state").GetString());

        var (deviceCode, shortCode) = await ClaimAsync(host, reference);
        var pending = await TestServerHost.JsonAsync(await host.PostAsync(Pair("status"), new { reference }, access));
        Assert.Equal("pending", pending.GetProperty("state").GetString());
        Assert.False(pending.TryGetProperty("shortCode", out _));
        using var duplicateClaim = await host.PostAsync(Pair("claim"), new { reference });
        Assert.Equal(HttpStatusCode.NotFound, duplicateClaim.StatusCode);
        using var wrongUser = await host.PostAsync(Pair("approve"), new { reference, shortCode }, TestServerHost.AccessToken(anotherUser));
        using var wrongDevice = await host.PostAsync(Pair("approve"), new { reference, shortCode }, TestServerHost.AccessToken(anotherDevice));
        using var wrongCode = await host.PostAsync(Pair("approve"), new { reference, shortCode = "wrong" }, access);
        Assert.Equal(HttpStatusCode.NotFound, wrongUser.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongDevice.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, wrongCode.StatusCode);
        using var approved = await host.PostAsync(Pair("approve"), new { reference, shortCode }, access);
        using var repeated = await host.PostAsync(Pair("approve"), new { reference, shortCode }, access);
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);

        var results = await Task.WhenAll(PollAsync(host, deviceCode), PollAsync(host, deviceCode));
        var mobile = Assert.Single(results, result => result.GetProperty("status").GetString() == "approved").GetProperty("session");
        Assert.Single(results, result => result.GetProperty("status").GetString() == "consumed");
        Assert.Equal(email, mobile.GetProperty("user").GetProperty("email").GetString());
        var mobileAccess = TestServerHost.AccessToken(mobile);
        var latest = await TestServerHost.JsonAsync(await host.GetAsync("/v1/backups/latest", mobileAccess));
        var snapshotId = latest.GetProperty("snapshotId").GetString();
        Assert.Equal(backup.GetProperty("snapshotId").GetString(), snapshotId);
        var snapshot = await TestServerHost.JsonAsync(await host.GetAsync($"/v1/backups/{snapshotId}", mobileAccess));
        Assert.Equal(tasks.Take(4).Order(), snapshot.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("key").GetString()).Order());
        var cursor = latest.GetProperty("cursor").GetString();
        var changes = await TestServerHost.JsonAsync(await host.GetAsync($"/v1/sync/changes?cursor={cursor}", mobileAccess));
        var change = Assert.Single(changes.GetProperty("changes").EnumerateArray());
        Assert.Equal(tasks[4], change.GetProperty("key").GetString());
        var log = string.Join('\n', host.Logs.Messages);
        Assert.DoesNotContain(reference, log, StringComparison.Ordinal);
        Assert.DoesNotContain(deviceCode, log, StringComparison.Ordinal);
        Assert.DoesNotContain(mobileAccess, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_claimed_pairing_expires_five_minutes_after_its_qr_was_created()
    {
        await using var host = await TestServerHost.StartAsync(ConfigureRelease, https: true);
        var (_, _, desktop) = await host.RegisterAsync();
        var access = TestServerHost.AccessToken(desktop);
        var reference = await StartAsync(host, access);
        host.Clock.Advance(TimeSpan.FromMinutes(4));
        var (deviceCode, shortCode) = await ClaimAsync(host, reference);
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        var status = await TestServerHost.JsonAsync(await host.PostAsync(Pair("status"), new { reference }, access));
        Assert.Equal("expired", status.GetProperty("state").GetString());
        using var approval = await host.PostAsync(Pair("approve"), new { reference, shortCode }, access);
        Assert.Equal(HttpStatusCode.Conflict, approval.StatusCode);
        var poll = await PollAsync(host, deviceCode);
        Assert.Equal("expired", poll.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, poll.GetProperty("session").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_or_signing_out_before_redemption_prevents_mobile_session(bool signOut)
    {
        await using var host = await TestServerHost.StartAsync(ConfigureRelease, https: true);
        var (_, _, desktop) = await host.RegisterAsync();
        var access = TestServerHost.AccessToken(desktop);
        var reference = await StartAsync(host, access);
        var (deviceCode, shortCode) = await ClaimAsync(host, reference);
        using var approval = await host.PostAsync(Pair("approve"), new { reference, shortCode }, access);
        Assert.Equal(HttpStatusCode.NoContent, approval.StatusCode);
        if (signOut)
        {
            using var logout = await host.PostAsync("/v1/auth/logout", new { refreshToken = TestServerHost.RefreshToken(desktop) }, access);
            Assert.True(logout.IsSuccessStatusCode);
        }
        else
        {
            using var cancel = await host.PostAsync(Pair("cancel"), new { reference }, access);
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }
        var poll = await PollAsync(host, deviceCode);
        Assert.Equal("denied", poll.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, poll.GetProperty("session").ValueKind);
    }

    [Fact]
    public async Task Missing_release_disables_pairing_and_download()
    {
        await using var host = await TestServerHost.StartAsync(https: true);
        using var pairing = await host.PostAsync(Pair("start"));
        using var download = await host.GetAsync("/device/mobile.apk");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pairing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    private static async Task<string> StartAsync(TestServerHost host, string access)
    {
        using var start = await host.PostAsync(Pair("start"), token: access);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var body = await TestServerHost.JsonAsync(start);
        var reference = body.GetProperty("reference").GetString()!;
        Assert.Equal("https://mosaic.test/device/mobile/connect#" + reference, body.GetProperty("url").GetString());
        return reference;
    }

    private static async Task<(string DeviceCode, string ShortCode)> ClaimAsync(TestServerHost host, string reference)
    {
        using var claim = await host.PostAsync(Pair("claim"), new { reference, deviceLabel = "Android Test", appVersion = "0.3.5" });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var result = await TestServerHost.JsonAsync(claim);
        return (result.GetProperty("deviceCode").GetString()!, result.GetProperty("shortCode").GetString()!);
    }

    private static async Task<JsonElement> PollAsync(TestServerHost host, string deviceCode)
    {
        using var response = await host.PostAsync(Pair("poll"), new { deviceCode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TestServerHost.JsonAsync(response);
    }

    private static string Pair(string operation) => "/v1/mobile/pairing/" + operation;

    private static void ConfigureRelease(ServerOptions options)
    {
        var contents = "仅测试 HTTP 下载行为"u8.ToArray();
        File.WriteAllBytes(Path.Combine(options.DataDirectory, "test.apk"), contents);
        options.PublicUrl = "https://mosaic.test";
        options.MobileReleaseManifest = Path.Combine(options.DataDirectory, "mobile.json");
        File.WriteAllText(options.MobileReleaseManifest, JsonSerializer.Serialize(new
        {
            apk = "test.apk", package = "app.mosaic.tasks",
            sha256 = Convert.ToHexString(SHA256.HashData(contents)), certificateSha256 = new string('A', 64),
        }));
    }
}
