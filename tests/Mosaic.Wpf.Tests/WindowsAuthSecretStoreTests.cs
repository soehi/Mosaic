using System.Text;
using Mosaic.Auth.Contracts;
using Mosaic.Auth.Windows;

namespace Mosaic.Wpf.Tests;

// Tokens must never exist as plain text on disk: the store is the only place they are persisted,
// and a file copied from another account or damaged by an interrupted write reads as "no session".
public sealed class WindowsAuthSecretStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mosaic-auth-tests", Guid.NewGuid().ToString("N"));
    private string Path_ => Path.Combine(_directory, "credentials.dat");

    private static AuthSecrets Secrets() => new(
        "refresh-token-very-secret",
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "user@example.com",
        "用户",
        new Uri("https://sync.example.com/"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Nothing_is_stored_before_the_first_sign_in()
    {
        var store = new WindowsAuthSecretStore(Path_);

        var loaded = await store.LoadAsync();

        Assert.True(loaded.IsSuccess);
        Assert.Null(loaded.Value);
    }

    [Fact]
    public async Task Saved_credentials_round_trip_with_the_identity_intact()
    {
        var store = new WindowsAuthSecretStore(Path_);

        var saved = await store.SaveAsync(Secrets());
        var loaded = await store.LoadAsync();

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.True(loaded.IsSuccess);
        Assert.Equal(Secrets(), loaded.Value);
    }

    [Fact]
    public async Task The_file_on_disk_is_not_plain_text()
    {
        var store = new WindowsAuthSecretStore(Path_);
        await store.SaveAsync(Secrets());

        var bytes = await File.ReadAllBytesAsync(Path_);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("refresh-token-very-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshToken", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(text.TrimStart().StartsWith('{'));
    }

    [Fact]
    public async Task Saving_again_replaces_the_previous_session()
    {
        var store = new WindowsAuthSecretStore(Path_);
        await store.SaveAsync(Secrets());

        var replacement = Secrets() with { RefreshToken = "second-token" };
        await store.SaveAsync(replacement);
        var loaded = await store.LoadAsync();

        Assert.Equal("second-token", loaded.Value!.RefreshToken);
        Assert.False(File.Exists(Path_ + ".tmp"));
    }

    [Fact]
    public async Task Clearing_removes_the_file_and_is_idempotent()
    {
        var store = new WindowsAuthSecretStore(Path_);
        await store.SaveAsync(Secrets());

        Assert.True((await store.ClearAsync()).IsSuccess);
        Assert.False(File.Exists(Path_));
        Assert.True((await store.ClearAsync()).IsSuccess);
        Assert.Null((await store.LoadAsync()).Value);
    }

    [Fact]
    public async Task A_damaged_file_reads_as_no_session()
    {
        var store = new WindowsAuthSecretStore(Path_);
        await store.SaveAsync(Secrets());
        await File.WriteAllBytesAsync(Path_, [1, 2, 3, 4, 5]);

        var loaded = await store.LoadAsync();

        Assert.True(loaded.IsSuccess);
        Assert.Null(loaded.Value);
    }

    [Fact]
    public async Task Plain_http_servers_and_empty_tokens_are_never_written()
    {
        var store = new WindowsAuthSecretStore(Path_);

        var insecure = await store.SaveAsync(Secrets() with { Server = new Uri("http://sync.example.com/") });
        var emptyToken = await store.SaveAsync(Secrets() with { RefreshToken = " " });

        Assert.Equal(AuthErrorCodes.SecretsUnavailable, insecure.Error!.Code);
        Assert.Equal(AuthErrorCodes.SecretsUnavailable, emptyToken.Error!.Code);
        Assert.False(File.Exists(Path_));
    }
}
