using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Mosaic.Server.Tests;

public sealed class AttachmentTests
{
    private static byte[] Payload(int size = 2048, byte seed = 7)
    {
        var bytes = new byte[size];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index * 31 + seed) % 251);
        }

        return bytes;
    }

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static Task<HttpResponseMessage> PutAttachmentAsync(
        TestServerHost host,
        string token,
        Guid attachmentId,
        Guid taskId,
        string fileName,
        byte[] bytes,
        string? sha256 = null)
    {
        var url = $"/v1/attachments/{attachmentId:D}?taskId={taskId:D}&fileName={Uri.EscapeDataString(fileName)}";
        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (sha256 is not null)
        {
            request.Headers.Add("X-Mosaic-Sha256", sha256);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return host.Client.SendAsync(request);
    }

    [Fact]
    public async Task Upload_manifest_and_download_round_trip()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);
        var attachmentId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var bytes = Payload();

        var upload = await PutAttachmentAsync(
            host, token, attachmentId, taskId, "会议纪要.txt", bytes, Sha256Of(bytes));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var uploaded = await TestServerHost.JsonAsync(upload);
        Assert.False(uploaded.GetProperty("duplicate").GetBoolean());
        Assert.Equal(attachmentId.ToString("D"), uploaded.GetProperty("id").GetString());
        Assert.Equal(taskId.ToString("D"), uploaded.GetProperty("taskId").GetString());
        Assert.Equal("会议纪要.txt", uploaded.GetProperty("fileName").GetString());
        Assert.Equal(bytes.Length, uploaded.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(Sha256Of(bytes), uploaded.GetProperty("sha256").GetString());

        var manifestResponse = await host.GetAsync("/v1/attachments", token);
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        var manifest = await TestServerHost.JsonAsync(manifestResponse);
        var items = manifest.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(attachmentId.ToString("D"), items[0].GetProperty("id").GetString());
        Assert.Equal(bytes.Length, manifest.GetProperty("usageBytes").GetInt64());
        Assert.Equal(host.Options.AttachmentQuotaBytes, manifest.GetProperty("quotaBytes").GetInt64());
        Assert.Equal(host.Options.AttachmentMaxBytes, manifest.GetProperty("maxBytes").GetInt64());

        var download = await host.GetAsync($"/v1/attachments/{attachmentId:D}", token);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("会议纪要.txt", download.Content.Headers.ContentDisposition?.FileNameStar
            ?? download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    [Fact]
    public async Task Identical_retry_is_deduplicated_without_rewriting()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);
        var attachmentId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var bytes = Payload(seed: 11);

        var first = await PutAttachmentAsync(host, token, attachmentId, taskId, "a.bin", bytes);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var createdAt = (await TestServerHost.JsonAsync(first)).GetProperty("createdAt").GetString();

        host.Clock.Advance(TimeSpan.FromMinutes(5));

        var second = await PutAttachmentAsync(host, token, attachmentId, taskId, "a.bin", bytes);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var retried = await TestServerHost.JsonAsync(second);
        Assert.True(retried.GetProperty("duplicate").GetBoolean());
        Assert.Equal(createdAt, retried.GetProperty("createdAt").GetString());

        var directory = Path.Combine(host.DataDirectory, "attachments");
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        Assert.Single(files);
        Assert.EndsWith(attachmentId.ToString("N") + ".bin", files[0]);

        var manifest = await TestServerHost.JsonAsync(await host.GetAsync("/v1/attachments", token));
        Assert.Equal(bytes.Length, manifest.GetProperty("usageBytes").GetInt64());
    }

    [Fact]
    public async Task Hash_mismatch_is_rejected_and_leaves_no_blob()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var response = await PutAttachmentAsync(
            host, token, Guid.NewGuid(), Guid.NewGuid(), "b.bin", Payload(), new string('0', 64));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await TestServerHost.JsonAsync(response);
        Assert.Equal("attachment.hash_mismatch", TestServerHost.ErrorCode(body));

        var directory = Path.Combine(host.DataDirectory, "attachments");
        Assert.Empty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
        var manifest = await TestServerHost.JsonAsync(await host.GetAsync("/v1/attachments", token));
        Assert.Equal(0, manifest.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Invalid_parameters_are_rejected()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var badName = await PutAttachmentAsync(host, token, Guid.NewGuid(), Guid.NewGuid(), "..", Payload());
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
        Assert.Equal("attachment.invalid", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(badName)));

        var badTask = await PutAttachmentAsync(host, token, Guid.NewGuid(), Guid.Empty, "c.bin", Payload());
        Assert.Equal(HttpStatusCode.BadRequest, badTask.StatusCode);
        Assert.Equal("attachment.invalid", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(badTask)));

        var badHash = await PutAttachmentAsync(
            host, token, Guid.NewGuid(), Guid.NewGuid(), "c.bin", Payload(), "not-a-hash");
        Assert.Equal(HttpStatusCode.BadRequest, badHash.StatusCode);
        Assert.Equal("attachment.invalid", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(badHash)));
    }

    [Fact]
    public async Task Attachment_id_collision_across_users_is_a_conflict()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, owner) = await host.RegisterAsync();
        var (_, _, other) = await host.RegisterAsync();
        var ownerToken = TestServerHost.AccessToken(owner);
        var otherToken = TestServerHost.AccessToken(other);
        var attachmentId = Guid.NewGuid();

        Assert.Equal(
            HttpStatusCode.Created,
            (await PutAttachmentAsync(host, ownerToken, attachmentId, Guid.NewGuid(), "x.bin", Payload())).StatusCode);

        var collision = await PutAttachmentAsync(
            host, otherToken, attachmentId, Guid.NewGuid(), "x.bin", Payload(seed: 3));
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        Assert.Equal("attachment.conflict", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(collision)));

        // The intruder can neither list nor download the owner's blob.
        var manifest = await TestServerHost.JsonAsync(await host.GetAsync("/v1/attachments", otherToken));
        Assert.Equal(0, manifest.GetProperty("items").GetArrayLength());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await host.GetAsync($"/v1/attachments/{attachmentId:D}", otherToken)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await host.GetAsync($"/v1/attachments/{attachmentId:D}", ownerToken)).StatusCode);
    }

    [Fact]
    public async Task Oversized_single_attachment_is_rejected()
    {
        await using var host = await TestServerHost.StartAsync(options =>
        {
            options.AttachmentMaxBytes = 1024;
            options.AttachmentQuotaBytes = 4096;
        });
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        var response = await PutAttachmentAsync(host, token, Guid.NewGuid(), Guid.NewGuid(), "big.bin", Payload(2048));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("attachment.too_large", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(response)));
    }

    [Fact]
    public async Task Quota_exhaustion_is_rejected()
    {
        await using var host = await TestServerHost.StartAsync(options =>
        {
            options.AttachmentMaxBytes = 64;
            options.AttachmentQuotaBytes = 100;
        });
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        Assert.Equal(
            HttpStatusCode.Created,
            (await PutAttachmentAsync(host, token, Guid.NewGuid(), Guid.NewGuid(), "one.bin", Payload(60))).StatusCode);

        var second = await PutAttachmentAsync(
            host, token, Guid.NewGuid(), Guid.NewGuid(), "two.bin", Payload(60, seed: 2));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, second.StatusCode);
        Assert.Equal("attachment.quota_exceeded", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(second)));

        var manifest = await TestServerHost.JsonAsync(await host.GetAsync("/v1/attachments", token));
        Assert.Equal(1, manifest.GetProperty("items").GetArrayLength());
        Assert.Equal(60, manifest.GetProperty("usageBytes").GetInt64());
    }

    [Fact]
    public async Task Delete_removes_row_and_blob()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);
        var attachmentId = Guid.NewGuid();

        Assert.Equal(
            HttpStatusCode.Created,
            (await PutAttachmentAsync(host, token, attachmentId, Guid.NewGuid(), "d.bin", Payload())).StatusCode);

        var delete = await host.SendAsync(HttpMethod.Delete, $"/v1/attachments/{attachmentId:D}", token: token);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var missing = await host.GetAsync($"/v1/attachments/{attachmentId:D}", token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("attachment.not_found", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(missing)));

        var manifest = await TestServerHost.JsonAsync(await host.GetAsync("/v1/attachments", token));
        Assert.Equal(0, manifest.GetProperty("items").GetArrayLength());
        Assert.Equal(0, manifest.GetProperty("usageBytes").GetInt64());
        Assert.Empty(Directory.GetFiles(
            Path.Combine(host.DataDirectory, "attachments"), "*", SearchOption.AllDirectories));

        // Deleting twice stays idempotent at the HTTP level.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await host.SendAsync(HttpMethod.Delete, $"/v1/attachments/{attachmentId:D}", token: token)).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_account_removes_attachment_blobs()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, password, session) = await host.RegisterAsync();
        var token = TestServerHost.AccessToken(session);

        Assert.Equal(
            HttpStatusCode.Created,
            (await PutAttachmentAsync(host, token, Guid.NewGuid(), Guid.NewGuid(), "e.bin", Payload())).StatusCode);
        var userDirectory = Assert.Single(Directory.GetDirectories(Path.Combine(host.DataDirectory, "attachments")));
        Assert.NotEmpty(Directory.GetFiles(userDirectory));

        var delete = await host.PostAsync("/v1/auth/account/delete", new { password }, token);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.False(Directory.Exists(userDirectory));
    }
}