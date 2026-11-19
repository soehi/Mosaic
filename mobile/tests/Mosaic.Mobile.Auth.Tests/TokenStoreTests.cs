using System.Text;
using Mosaic.Mobile.Auth;

namespace Mosaic.Mobile.Auth.Tests;

public sealed class TokenStoreTests
{
    private static StoredSession Session(string server = "https://example.test") => new()
    {
        ServerUrl = server,
        DeviceId = "device-1",
        AccountEmail = "user@example.com",
        UserId = "user-1",
        RefreshToken = "refresh-1",
        RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
    };

    [Fact]
    public async Task Round_trip_preserves_metadata_and_never_access_token()
    {
        var storage = new InMemorySecretStorage();
        var store = new TokenStore(storage);

        await store.SaveAsync(Session(), CancellationToken.None);
        var loaded = await store.LoadAsync("https://example.test", CancellationToken.None);

        Assert.Equal(SessionStoreStatus.Loaded, loaded.Status);
        Assert.Equal("refresh-1", loaded.Session?.RefreshToken);
        Assert.Equal("device-1", loaded.Session?.DeviceId);
        Assert.Equal("user@example.com", loaded.Session?.AccountEmail);

        var raw = Encoding.UTF8.GetString(storage.Find("mosaic.session.")!);
        Assert.DoesNotContain("accessToken", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Another_server_never_reads_the_record()
    {
        var storage = new InMemorySecretStorage();
        var store = new TokenStore(storage);
        await store.SaveAsync(Session("https://one.test"), CancellationToken.None);

        var other = await store.LoadAsync("https://two.test", CancellationToken.None);

        Assert.Equal(SessionStoreStatus.Missing, other.Status);
    }

    [Fact]
    public async Task Corrupted_record_is_deleted_and_reported_unavailable()
    {
        var storage = new InMemorySecretStorage();
        var store = new TokenStore(storage);
        await store.SaveAsync(Session(), CancellationToken.None);
        storage.Corrupt("mosaic.session.");

        var loaded = await store.LoadAsync("https://example.test", CancellationToken.None);

        Assert.Equal(SessionStoreStatus.Unavailable, loaded.Status);
        Assert.Equal("session_record_corrupt", loaded.Diagnostic);
        Assert.Null(storage.Find("mosaic.session."));
    }

    [Fact]
    public async Task Read_failure_is_unavailable_not_an_exception()
    {
        var storage = new InMemorySecretStorage { FailReads = true };
        var store = new TokenStore(storage);

        var loaded = await store.LoadAsync("https://example.test", CancellationToken.None);

        Assert.Equal(SessionStoreStatus.Unavailable, loaded.Status);
        Assert.Equal("secure_storage_unavailable", loaded.Diagnostic);
    }

    [Fact]
    public async Task Write_failure_throws_the_typed_exception()
    {
        var storage = new InMemorySecretStorage { FailWrites = true };
        var store = new TokenStore(storage);

        await Assert.ThrowsAsync<SessionStoreUnavailableException>(
            () => store.SaveAsync(Session(), CancellationToken.None));
    }

    [Fact]
    public async Task Device_id_is_stable_and_minted_once()
    {
        var storage = new InMemorySecretStorage();
        var store = new TokenStore(storage);

        var first = await store.GetOrCreateDeviceIdAsync(CancellationToken.None);
        var second = await store.GetOrCreateDeviceIdAsync(CancellationToken.None);

        Assert.True(Guid.TryParse(first, out _));
        Assert.Equal(first, second);
    }
}
