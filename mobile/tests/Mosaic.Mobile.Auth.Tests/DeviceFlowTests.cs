using System.Net;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Auth.Tests;

public sealed class DeviceFlowTests
{
    private static bool Is(RecordedRequest request, string suffix) =>
        request.Uri.EndsWith(suffix, StringComparison.Ordinal);

    private static readonly DeviceStartResponseDto Start = new()
    {
        DeviceCode = "device-code-1",
        UserCode = "BCDFGHJK",
        VerificationUri = "https://example.test/device",
        ExpiresIn = 600,
        Interval = 1,
    };

    private static (DeviceFlowPollingSession Session, StubHandler Handler, TestTimeProvider Time) Create(
        Func<RecordedRequest, HttpResponseMessage> responder,
        DeviceStartResponseDto? start = null)
    {
        var handler = new StubHandler(responder);
        var api = TestApi.Create(handler);
        var time = new TestTimeProvider();
        var client = new DeviceFlowClient(api);
        var session = new DeviceFlowPollingSession(
            client,
            start ?? Start,
            time,
            (delay, _) =>
            {
                time.Advance(delay);
                return Task.CompletedTask;
            });
        return (session, handler, time);
    }

    private static HttpResponseMessage Status(string status) =>
        Responses.Json(HttpStatusCode.OK, $$"""{"status":"{{status}}"}""");

    private static HttpResponseMessage Approved(DateTimeOffset expires) =>
        Responses.Json(
            HttpStatusCode.OK,
            $$"""{"status":"approved","session":{{Responses.Session(accessExpires: expires)}}}""");

    [Fact]
    public async Task Pending_then_approved_returns_the_session()
    {
        var polls = 0;
        var time = new TestTimeProvider();
        var (session, _, _) = Create(request =>
        {
            Assert.True(Is(request, "/v1/device/poll"));
            polls++;
            return polls < 3 ? Status("pending") : Approved(time.GetUtcNow().AddHours(1));
        });

        var result = await session.RunAsync();

        Assert.Equal(DeviceFlowState.Approved, result.State);
        Assert.NotNull(result.Session);
        Assert.Equal("access-1", result.Session.AccessToken);
        Assert.Equal(3, polls);
        Assert.Equal("BCDF-GHJK", session.DisplayUserCode);
    }

    [Fact]
    public async Task Slow_down_increases_the_interval()
    {
        var polls = 0;
        var (session, _, _) = Create(_ =>
        {
            polls++;
            return polls == 1 ? Status("slow_down") : Status("denied");
        });

        var result = await session.RunAsync();

        Assert.Equal(DeviceFlowState.Denied, result.State);
        Assert.True(session.Interval >= TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task Rate_limited_honours_retry_after_and_stays_pending()
    {
        var polls = 0;
        var (session, _, _) = Create(_ =>
        {
            polls++;
            return polls == 1
                ? Responses.RateLimited("device.rate_limited", 30)
                : Status("denied");
        });

        var result = await session.RunAsync();

        Assert.Equal(DeviceFlowState.Denied, result.State);
        Assert.True(session.Interval >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Expiry_stops_polling_without_waiting_for_a_terminal_status()
    {
        var polls = 0;
        var (session, _, _) = Create(
            _ =>
            {
                polls++;
                return Status("pending");
            },
            Start with { ExpiresIn = 60, Interval = 30 });

        var result = await session.RunAsync();

        Assert.Equal(DeviceFlowState.Expired, result.State);
        Assert.Equal(1, polls);
    }

    [Fact]
    public async Task Network_error_keeps_polling_until_approved()
    {
        var polls = 0;
        var time = new TestTimeProvider();
        var (session, _, _) = Create(_ =>
        {
            polls++;
            if (polls == 1)
            {
                throw new HttpRequestException("offline");
            }

            return Approved(time.GetUtcNow().AddHours(1));
        });

        var result = await session.RunAsync();

        Assert.Equal(DeviceFlowState.Approved, result.State);
        Assert.Equal(2, polls);
        Assert.NotEmpty(session.Diagnostics);
    }

    [Fact]
    public async Task Unknown_status_is_recorded_but_does_not_end_the_flow()
    {
        var polls = 0;
        var (session, _, _) = Create(_ =>
        {
            polls++;
            return polls == 1 ? Status("brand_new") : Status("consumed");
        });

        var result = await session.RunAsync();

        Assert.Equal(DeviceFlowState.Consumed, result.State);
        Assert.Contains(session.Diagnostics, value => value.Contains("brand_new", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Approved_without_session_is_a_protocol_error()
    {
        var (session, _, _) = Create(_ => Status("approved"));

        var result = await session.RunAsync();

        Assert.Equal(AuthErrorKind.Protocol, result.Error?.Kind);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task Stop_cancels_without_polling()
    {
        var polls = 0;
        var handler = new StubHandler(_ =>
        {
            polls++;
            return Status("pending");
        });
        using var api = TestApi.Create(handler);
        var session = new DeviceFlowPollingSession(
            new DeviceFlowClient(api),
            Start with { Interval = 1 },
            new TestTimeProvider());

        var run = session.RunAsync();
        session.Stop();
        var result = await run;

        Assert.Equal(AuthErrorKind.Cancelled, result.Error?.Kind);
        Assert.Equal(0, polls);
    }

    [Theory]
    [InlineData("bcdf-ghjk", "BCDFGHJK")]
    [InlineData("BCDF GHJK", "BCDFGHJK")]
    [InlineData("BCDFGHJK", "BCDFGHJK")]
    [InlineData("BCDF-GHJ", null)]
    [InlineData("BCDF-OHJK", null)]
    [InlineData("BCDF-0HJK", null)]
    public void User_code_normalization_matches_the_server(string input, string? expected)
    {
        Assert.Equal(expected, DeviceUserCodes.Normalize(input));
    }

    [Fact]
    public async Task Approval_service_rejects_invalid_code_before_network()
    {
        var handler = new StubHandler(_ => Responses.NoContent());
        using var api = TestApi.Create(handler);
        var service = new DeviceApprovalService(new DeviceFlowClient(api));

        var result = await service.ApproveAsync("bad");

        Assert.False(result.Succeeded);
        Assert.Equal(AuthErrorKind.DeviceCodeInvalid, result.Error?.Kind);
        Assert.Empty(handler.Requests);
    }
}
