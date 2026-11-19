using System.Net;
using System.Text;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Auth.Tests;

public sealed class AuthSessionManagerTests
{
    private static bool Is(RecordedRequest request, string suffix) =>
        request.Uri.EndsWith(suffix, StringComparison.Ordinal);

    [Fact]
    public async Task Sign_in_persists_refresh_token_only_and_sets_signed_in()
    {
        var time = new TestTimeProvider();
        var storage = new InMemorySecretStorage();
        var handler = new StubHandler(request => Is(request, "/v1/auth/login")
            ? Responses.Json(
                HttpStatusCode.OK,
                Responses.Session(
                    access: "access-1",
                    refresh: "refresh-1",
                    accessExpires: time.GetUtcNow().AddHours(1)))
            : Responses.Error(HttpStatusCode.NotFound, "server.not_found"));

        using var api = TestApi.Create(handler);
        var store = new TokenStore(storage, time);
        using var manager = new AuthSessionManager(api, store, time);
        await manager.InitializeAsync("https://example.test");

        var result = await manager.SignInAsync("user@example.com", "password1234", "Pixel");

        Assert.True(result.Succeeded);
        Assert.Equal(AuthState.SignedIn, manager.State);
        Assert.Equal("access-1", manager.AccessToken);
        var raw = Encoding.UTF8.GetString(storage.Find("mosaic.session.")!);
        Assert.DoesNotContain("access-1", raw, StringComparison.Ordinal);
        Assert.Contains("refresh-1", raw, StringComparison.Ordinal);

        var load = await store.LoadAsync("https://example.test", CancellationToken.None);
        Assert.Equal(SessionStoreStatus.Loaded, load.Status);
    }

    [Fact]
    public async Task Sign_in_401_maps_to_invalid_credentials()
    {
        var time = new TestTimeProvider();
        var handler = new StubHandler(_ => Responses.Error(
            HttpStatusCode.Unauthorized,
            "auth.invalid_credentials"));

        using var api = TestApi.Create(handler);
        using var manager = new AuthSessionManager(api, new TokenStore(new InMemorySecretStorage(), time), time);
        await manager.InitializeAsync("https://example.test");

        var result = await manager.SignInAsync("user@example.com", "wrong-password", "Pixel");

        Assert.False(result.Succeeded);
        Assert.Equal(AuthErrorKind.InvalidCredentials, result.Error?.Kind);
        Assert.Equal(AuthState.SignedOut, manager.State);
    }

    [Fact]
    public async Task Sign_in_storage_failure_reports_unavailable_and_stays_signed_out()
    {
        var time = new TestTimeProvider();
        var handler = new StubHandler(_ => Responses.Json(
            HttpStatusCode.OK,
            Responses.Session(accessExpires: time.GetUtcNow().AddHours(1))));
        var storage = new InMemorySecretStorage { FailWrites = true };

        using var api = TestApi.Create(handler);
        using var manager = new AuthSessionManager(api, new TokenStore(storage, time), time);
        await manager.InitializeAsync("https://example.test");

        var result = await manager.SignInAsync("user@example.com", "password1234", "Pixel");

        Assert.False(result.Succeeded);
        Assert.Equal(AuthErrorKind.StorageUnavailable, result.Error?.Kind);
        Assert.Equal(AuthState.SignedOut, manager.State);
    }

    [Fact]
    public async Task Concurrent_refresh_calls_share_one_network_request()
    {
        var time = new TestTimeProvider();
        var refreshCount = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async request =>
        {
            if (Is(request, "/v1/auth/login"))
            {
                return Responses.Json(
                    HttpStatusCode.OK,
                    Responses.Session(
                        access: "access-1",
                        refresh: "refresh-1",
                        accessExpires: time.GetUtcNow().AddMinutes(-1)));
            }

            if (Is(request, "/v1/auth/refresh"))
            {
                Interlocked.Increment(ref refreshCount);
                await gate.Task;
                return Responses.Json(
                    HttpStatusCode.OK,
                    Responses.Session(
                        access: "access-2",
                        refresh: "refresh-2",
                        accessExpires: time.GetUtcNow().AddHours(1)));
            }

            return Responses.Error(HttpStatusCode.NotFound, "server.not_found");
        });

        using var api = TestApi.Create(handler);
        var storage = new InMemorySecretStorage();
        var store = new TokenStore(storage, time);
        using var manager = new AuthSessionManager(api, store, time);
        await manager.InitializeAsync("https://example.test");
        var signIn = await manager.SignInAsync("user@example.com", "password1234", "Pixel");
        Assert.True(signIn.Succeeded);

        var first = manager.RefreshAsync(CancellationToken.None);
        var second = manager.RefreshAsync(CancellationToken.None);
        await Task.Delay(50);
        gate.SetResult();
        var tokens = await Task.WhenAll(first, second);

        Assert.Equal(1, Volatile.Read(ref refreshCount));
        Assert.All(tokens, token => Assert.Equal("access-2", token));
        var load = await store.LoadAsync("https://example.test", CancellationToken.None);
        Assert.Equal("refresh-2", load.Session?.RefreshToken);
    }

    [Fact]
    public async Task Refresh_rejection_clears_tokens_and_marks_cache_read_only()
    {
        var time = new TestTimeProvider();
        var handler = new StubHandler(request =>
        {
            if (Is(request, "/v1/auth/login"))
            {
                return Responses.Json(
                    HttpStatusCode.OK,
                    Responses.Session(accessExpires: time.GetUtcNow().AddMinutes(-1)));
            }

            return Responses.Error(HttpStatusCode.Unauthorized, "auth.session_revoked");
        });

        using var api = TestApi.Create(handler);
        var store = new TokenStore(new InMemorySecretStorage(), time);
        using var manager = new AuthSessionManager(api, store, time);
        await manager.InitializeAsync("https://example.test");
        await manager.SignInAsync("user@example.com", "password1234", "Pixel");

        var token = await manager.RefreshAsync(CancellationToken.None);

        Assert.Null(token);
        Assert.Equal(AuthState.SessionExpired, manager.State);
        Assert.True(manager.CacheReadOnly);
        var load = await store.LoadAsync("https://example.test", CancellationToken.None);
        Assert.Equal(SessionStoreStatus.Missing, load.Status);
    }

    [Fact]
    public async Task Network_failure_keeps_session_for_offline_reading()
    {
        var time = new TestTimeProvider();
        var handler = new StubHandler(request =>
        {
            if (Is(request, "/v1/auth/login"))
            {
                return Responses.Json(
                    HttpStatusCode.OK,
                    Responses.Session(accessExpires: time.GetUtcNow().AddMinutes(-1)));
            }

            throw new HttpRequestException("offline");
        });

        using var api = TestApi.Create(handler);
        var store = new TokenStore(new InMemorySecretStorage(), time);
        using var manager = new AuthSessionManager(api, store, time);
        await manager.InitializeAsync("https://example.test");
        await manager.SignInAsync("user@example.com", "password1234", "Pixel");

        var token = await manager.RefreshAsync(CancellationToken.None);

        Assert.Null(token);
        Assert.Equal(AuthState.SignedIn, manager.State);
        Assert.False(manager.CacheReadOnly);
        var load = await store.LoadAsync("https://example.test", CancellationToken.None);
        Assert.Equal(SessionStoreStatus.Loaded, load.Status);
    }

    [Fact]
    public async Task Sign_out_calls_server_and_clears_local_session_and_cache()
    {
        var time = new TestTimeProvider();
        var handler = new StubHandler(request =>
        {
            if (Is(request, "/v1/auth/login"))
            {
                return Responses.Json(
                    HttpStatusCode.OK,
                    Responses.Session(accessExpires: time.GetUtcNow().AddHours(1)));
            }

            if (Is(request, "/v1/auth/logout"))
            {
                return Responses.NoContent();
            }

            return Responses.Error(HttpStatusCode.NotFound, "server.not_found");
        });

        using var api = TestApi.Create(handler);
        var storage = new InMemorySecretStorage();
        var store = new TokenStore(storage, time);
        using var manager = new AuthSessionManager(api, store, time);
        await manager.InitializeAsync("https://example.test");
        await manager.SignInAsync("user@example.com", "password1234", "Pixel");
        var clearedReasons = new List<string>();
        manager.LocalDataCleared += (_, reason) => clearedReasons.Add(reason);

        var result = await manager.SignOutAsync(allDevices: false, localOnly: false);

        Assert.True(result.Completed);
        Assert.Equal(AuthState.SignedOut, manager.State);
        Assert.Contains("sign_out", clearedReasons);
        Assert.Null(storage.Find("mosaic.session."));
        var logout = handler.Requests.Single(request => Is(request, "/v1/auth/logout"));
        Assert.Equal("Bearer access-1", logout.Authorization);
        Assert.Contains("refresh-1", logout.Body ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_out_local_only_makes_no_network_call()
    {
        var time = new TestTimeProvider();
        var handler = new StubHandler(request => Is(request, "/v1/auth/login")
            ? Responses.Json(
                HttpStatusCode.OK,
                Responses.Session(accessExpires: time.GetUtcNow().AddHours(1)))
            : Responses.NoContent());

        using var api = TestApi.Create(handler);
        var storage = new InMemorySecretStorage();
        using var manager = new AuthSessionManager(api, new TokenStore(storage, time), time);
        await manager.InitializeAsync("https://example.test");
        await manager.SignInAsync("user@example.com", "password1234", "Pixel");
        var before = handler.Requests.Count;

        var result = await manager.SignOutAsync(allDevices: true, localOnly: true);

        Assert.True(result.Completed);
        Assert.Equal(before, handler.Requests.Count);
        Assert.Null(storage.Find("mosaic.session."));
    }

    [Fact]
    public async Task Sign_out_server_unreachable_reports_failure_and_keeps_tokens()
    {
        var time = new TestTimeProvider();
        var handler = new StubHandler(request =>
        {
            if (Is(request, "/v1/auth/login"))
            {
                return Responses.Json(
                    HttpStatusCode.OK,
                    Responses.Session(accessExpires: time.GetUtcNow().AddHours(1)));
            }

            throw new HttpRequestException("offline");
        });

        using var api = TestApi.Create(handler);
        var storage = new InMemorySecretStorage();
        var store = new TokenStore(storage, time);
        using var manager = new AuthSessionManager(api, store, time);
        await manager.InitializeAsync("https://example.test");
        await manager.SignInAsync("user@example.com", "password1234", "Pixel");

        var result = await manager.SignOutAsync(allDevices: false, localOnly: false);

        Assert.False(result.Completed);
        Assert.Equal(AuthErrorKind.Network, result.Error?.Kind);
        Assert.NotNull(storage.Find("mosaic.session."));
    }

    [Fact]
    public async Task Password_reset_clears_tokens_but_keeps_cache_read_only()
    {
        var time = new TestTimeProvider();
        var handler = new StubHandler(request =>
        {
            if (Is(request, "/v1/auth/login"))
            {
                return Responses.Json(
                    HttpStatusCode.OK,
                    Responses.Session(accessExpires: time.GetUtcNow().AddHours(1)));
            }

            return Responses.Json(HttpStatusCode.Accepted, """{"status":"queued"}""");
        });

        using var api = TestApi.Create(handler);
        var storage = new InMemorySecretStorage();
        using var manager = new AuthSessionManager(api, new TokenStore(storage, time), time);
        await manager.InitializeAsync("https://example.test");
        await manager.SignInAsync("user@example.com", "password1234", "Pixel");

        var result = await manager.ResetPasswordAsync("user@example.com", "123456", "new-password-123");

        Assert.True(result.Succeeded);
        Assert.Equal(AuthState.SessionExpired, manager.State);
        Assert.True(manager.CacheReadOnly);
        Assert.Null(storage.Find("mosaic.session."));
    }

    [Fact]
    public async Task Delete_account_clears_local_data_and_signs_out()
    {
        var time = new TestTimeProvider();
        var handler = new StubHandler(request =>
        {
            if (Is(request, "/v1/auth/login"))
            {
                return Responses.Json(
                    HttpStatusCode.OK,
                    Responses.Session(accessExpires: time.GetUtcNow().AddHours(1)));
            }

            if (Is(request, "/v1/auth/account/delete"))
            {
                return Responses.NoContent();
            }

            return Responses.Error(HttpStatusCode.NotFound, "server.not_found");
        });

        using var api = TestApi.Create(handler);
        var storage = new InMemorySecretStorage();
        using var manager = new AuthSessionManager(api, new TokenStore(storage, time), time);
        await manager.InitializeAsync("https://example.test");
        await manager.SignInAsync("user@example.com", "password1234", "Pixel");
        var cleared = new List<string>();
        manager.LocalDataCleared += (_, reason) => cleared.Add(reason);

        var result = await manager.DeleteAccountAsync("password1234", null);

        Assert.True(result.Succeeded);
        Assert.Equal(AuthState.SignedOut, manager.State);
        Assert.Contains("account_deleted", cleared);
        var request = handler.Requests.Single(r => Is(r, "/v1/auth/account/delete"));
        Assert.Equal("Bearer access-1", request.Authorization);
    }

    [Fact]
    public async Task Delete_account_requires_password_or_code()
    {
        using var api = TestApi.Create(new StubHandler(_ => Responses.NoContent()));
        using var manager = new AuthSessionManager(
            api,
            new TokenStore(new InMemorySecretStorage()),
            new TestTimeProvider());

        var result = await manager.DeleteAccountAsync(null, null);

        Assert.False(result.Succeeded);
        Assert.Equal(AuthErrorKind.ReauthRequired, result.Error?.Kind);
    }

    [Fact]
    public async Task Sessions_list_parses_envelope_without_tokens()
    {
        var handler = new StubHandler(request => Is(request, "/v1/auth/sessions")
            ? Responses.Json(
                HttpStatusCode.OK,
                """
                {"sessions":[
                  {"id":"s1","deviceLabel":"Pixel","platform":"android","createdAt":"2026-11-19T09:00:00Z","current":true},
                  {"id":"s2","deviceLabel":"Desktop","platform":"windows","createdAt":"2026-11-18T09:00:00Z","current":false}
                ]}
                """)
            : Responses.Error(HttpStatusCode.NotFound, "server.not_found"));

        using var api = TestApi.Create(handler);
        using var manager = new AuthSessionManager(
            api,
            new TokenStore(new InMemorySecretStorage()),
            new TestTimeProvider());
        await manager.InitializeAsync("https://example.test");

        var result = await manager.GetSessionsAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Value?.Count);
        Assert.Equal("Pixel", result.Value![0].DeviceLabel);
        Assert.True(result.Value[0].Current);
    }

    [Fact]
    public async Task Device_session_is_persisted_before_identity_verification()
    {
        var time = new TestTimeProvider();
        var handler = new StubHandler(request => Is(request, "/v1/auth/me")
            ? Responses.Json(HttpStatusCode.OK, """{"user":{"id":"user-1","email":"user@example.com"}}""")
            : Responses.Error(HttpStatusCode.NotFound, "server.not_found"));

        using var api = TestApi.Create(handler);
        var storage = new InMemorySecretStorage();
        var store = new TokenStore(storage, time);
        using var manager = new AuthSessionManager(api, store, time);
        await manager.InitializeAsync("https://example.test");

        var session = WireJson.Deserialize<SessionDto>(
            Responses.Session(accessExpires: time.GetUtcNow().AddHours(1)))!;
        var result = await manager.CompleteDeviceSignInAsync(session);

        Assert.True(result.Succeeded);
        Assert.Equal(AuthState.SignedIn, manager.State);
        Assert.NotNull(storage.Find("mosaic.session."));
        Assert.Equal("user@example.com", manager.AccountEmail);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    [InlineData("ftp://example.test", false)]
    [InlineData("http://example.com", false)]
    [InlineData("http://localhost:5199", true)]
    [InlineData("https://example.test/", true)]
    [InlineData("https://example.test/v1", true)]
    public void Server_address_rules(string input, bool expected)
    {
        var valid = ServerAddress.TryNormalize(input, out var normalized, out _);

        Assert.Equal(expected, valid);
        if (valid)
        {
            Assert.False(normalized.EndsWith("/v1", StringComparison.Ordinal));
            Assert.False(normalized.EndsWith('/'));
        }
    }

    [Fact]
    public async Task Configure_server_refuses_switch_while_signed_in()
    {
        var time = new TestTimeProvider();
        var handler = new StubHandler(_ => Responses.Json(
            HttpStatusCode.OK,
            Responses.Session(accessExpires: time.GetUtcNow().AddHours(1))));
        using var api = TestApi.Create(handler);
        using var manager = new AuthSessionManager(api, new TokenStore(new InMemorySecretStorage(), time), time);
        await manager.InitializeAsync("https://example.test");
        await manager.SignInAsync("user@example.com", "password1234", "Pixel");

        var result = manager.ConfigureServer("https://other.test");

        Assert.False(result.Succeeded);
        Assert.Equal("server.switch_requires_sign_out", result.Error?.Code);
    }
}
