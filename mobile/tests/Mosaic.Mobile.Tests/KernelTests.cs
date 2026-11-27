using System.Text.Json;
using Mosaic.Mobile.Core;
using Mosaic.Mobile.Core.Auth;
using Mosaic.Mobile.Core.Cards;
using Mosaic.Mobile.Core.Configuration;
using Mosaic.Mobile.Core.Projection;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Tests;

public sealed class KernelTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Unconfigured_server_renders_a_configuration_card()
    {
        var harness = CreateHarness(configured: false);
        var model = await harness.Kernel.RefreshAsync(CancellationToken.None);

        Assert.Equal(CardState.NeedsConfiguration, model.State);
        Assert.Empty(harness.Api.Requests);
    }

    [Fact]
    public async Task Missing_session_renders_the_sign_in_state()
    {
        var harness = CreateHarness(configured: true, withSession: false);
        var model = await harness.Kernel.RefreshAsync(CancellationToken.None);

        Assert.Equal(CardState.NeedsSignIn, model.State);
    }

    [Fact]
    public async Task Offline_restore_keeps_cached_rows_visible()
    {
        var harness = CreateHarness(configured: true, withSession: true);
        SeedProjection(harness.Configuration, Sample.Task(Sample.TaskId, Sample.Payload(Sample.TaskId)));
        harness.Api.Responder = (_, path, _) => path switch
        {
            "v1/auth/me" => FakeMosaicApi.Transport<MeDto>(),
            _ => FakeMosaicApi.Transport<SyncChangePageDto>(),
        };

        var model = await harness.Kernel.RefreshAsync(CancellationToken.None);

        Assert.Equal(CardState.Offline, model.State);
        Assert.Single(model.Rows);
        Assert.Equal("整理周报", model.Rows[0].Title);
        Assert.StartsWith("离线", model.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ready_card_lists_unarchived_tasks_after_a_pull()
    {
        var harness = CreateHarness(configured: true, withSession: true);
        SeedProjection(harness.Configuration, Sample.Task(Sample.TaskId, Sample.Payload(Sample.TaskId)));
        harness.Api.Responder = (method, path, _) => path switch
        {
            "v1/auth/me" => FakeMosaicApi.Ok(new MeDto { User = new UserDto { Id = "user-1" } }),
            _ when path.StartsWith("v1/sync/changes", StringComparison.Ordinal) && method == "GET" =>
                FakeMosaicApi.Ok(new SyncChangePageDto { Changes = [], NextCursor = "7", HasMore = false }),
            _ => throw new InvalidOperationException($"{method} {path}"),
        };

        var model = await harness.Kernel.RefreshAsync(CancellationToken.None);

        Assert.Equal(CardState.Ready, model.State);
        Assert.Single(model.Rows);
        Assert.Equal("1 项待办", model.Headline);
    }

    [Fact]
    public async Task Rejected_refresh_clears_tokens_and_reports_session_expired()
    {
        var harness = CreateHarness(configured: true, withSession: true, accessTokenExpired: true);
        harness.Api.Responder = (_, path, _) => path == "v1/auth/refresh"
            ? FakeMosaicApi.Failure<SessionDto>(401, WireErrorCodes.AuthInvalidRefresh)
            : throw new InvalidOperationException(path);

        var model = await harness.Kernel.RefreshAsync(CancellationToken.None);

        Assert.Equal(CardState.SessionExpired, model.State);
        Assert.Null(await harness.Tokens.LoadSessionAsync(harness.Configuration.NormalizedOrigin, CancellationToken.None));
    }

    [Fact]
    public async Task Archive_action_result_reflects_the_updated_card()
    {
        var harness = CreateHarness(configured: true, withSession: true);
        SeedProjection(
            harness.Configuration,
            Sample.Task(Sample.TaskId, Sample.Payload(Sample.TaskId)),
            Sample.Task(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Sample.Payload(Guid.Parse("22222222-2222-2222-2222-222222222222"), title: "第二项")));
        harness.Api.Responder = (method, path, body) => path switch
        {
            "v1/auth/me" => FakeMosaicApi.Ok(new MeDto { User = new UserDto { Id = "user-1" } }),
            _ when method == "GET" && path.StartsWith("v1/sync/changes", StringComparison.Ordinal) =>
                FakeMosaicApi.Ok(new SyncChangePageDto { Changes = [], NextCursor = "7", HasMore = false }),
            "v1/sync/changes" when method == "POST" => FakeMosaicApi.Applied((ChangePushRequestDto)body!),
            _ => throw new InvalidOperationException($"{method} {path}"),
        };

        var result = await harness.Kernel.ExecuteAsync(Sample.TaskId, CardAction.Archive, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Model.Rows);
        Assert.Equal("第二项", result.Model.Rows[0].Title);
    }

    private Harness CreateHarness(bool configured, bool withSession = false, bool accessTokenExpired = false)
    {
        var secrets = new InMemorySecretStorage();
        var tokens = new TokenStore(secrets);
        var api = new FakeMosaicApi();
        var configuration = FixedServerConfigProvider.CreateConfiguration();
        var session = new SessionManager(new FakeApiFactory(api), tokens);
        var config = new FixedServerConfigProvider { Configuration = configured ? configuration : null };
        var kernel = new MobileKernel(config, session, new FakeApiFactory(api), _directory.Path, TimeProvider.System, new UnavailableFluidCloudAdapter());

        if (withSession)
        {
            var now = DateTimeOffset.UtcNow;
            tokens.SaveSessionAsync(
                new StoredSession
                {
                    ServerOrigin = configuration.NormalizedOrigin,
                    UserId = "user-1",
                    AccessToken = "access",
                    RefreshToken = "refresh",
                    AccessTokenExpiresAt = accessTokenExpired ? now.AddSeconds(-1) : now.AddHours(1),
                    RefreshTokenExpiresAt = now.AddDays(30),
                    DeviceId = "device-1",
                },
                CancellationToken.None).GetAwaiter().GetResult();
        }

        return new Harness(kernel, api, tokens, configuration);
    }

    private void SeedProjection(ServerConfiguration configuration, params SyncedTask[] tasks) =>
        Sample.SeededProjection(_directory.Path, ProjectionIdentity.Create(configuration.NormalizedOrigin, "user-1"), tasks);

    private sealed record Harness(MobileKernel Kernel, FakeMosaicApi Api, TokenStore Tokens, ServerConfiguration Configuration);
}