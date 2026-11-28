using Mosaic.Mobile.Core.Cards;
using Mosaic.Mobile.Core.Projection;

namespace Mosaic.Mobile.Tests;

public sealed class CardTests
{
    [Fact]
    public void Only_three_actions_are_accepted()
    {
        Assert.True(CardActionCodec.TryParse("complete", out var complete));
        Assert.Equal(CardAction.Complete, complete);
        Assert.True(CardActionCodec.TryParse("ARCHIVE", out var archive));
        Assert.Equal(CardAction.Archive, archive);
        Assert.True(CardActionCodec.TryParse("refresh", out var refresh));
        Assert.Equal(CardAction.Refresh, refresh);

        Assert.False(CardActionCodec.TryParse("restore", out _));
        Assert.False(CardActionCodec.TryParse("delete", out _));
        Assert.False(CardActionCodec.TryParse(null, out _));
        Assert.False(CardActionCodec.TryParseTaskAction(CardAction.Refresh, out _));
        Assert.Equal(CardAction.Refresh, CardActionCodec.TryParse("REFRESH", out var upper) ? upper : default);
    }

    [Fact]
    public void Request_codes_are_stable_and_distinct_per_action()
    {
        var complete = CardActionCodec.RequestCode(Sample.TaskId, CardAction.Complete);
        var archive = CardActionCodec.RequestCode(Sample.TaskId, CardAction.Archive);

        Assert.Equal(complete, CardActionCodec.RequestCode(Sample.TaskId, CardAction.Complete));
        Assert.NotEqual(complete, archive);
        Assert.NotEqual(
            complete,
            CardActionCodec.RequestCode(Guid.Parse("22222222-2222-2222-2222-222222222222"), CardAction.Complete));
        Assert.True(complete >= 0);
        Assert.True(archive >= 0);
    }

    [Fact]
    public void Card_model_carries_no_notes_or_account_identity()
    {
        var status = FluidCloudStatus.Blocked("fluidcloud.oppo_credentials_missing");
        var model = CardModelBuilder.Ready(
            new ProjectionCounts(2, 1, 0),
            [new CardTaskRow(Sample.TaskId, "整理周报", false)],
            status);

        var text = string.Join('\n', model.Headline, model.Detail, string.Join('\n', model.Rows.Select(row => row.Title)));
        Assert.DoesNotContain("备注", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@", text, StringComparison.Ordinal);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1 项待办", model.Headline);
        Assert.Equal("共 2 项未归档", model.Detail);
    }

    [Fact]
    public void Fluid_cloud_blocked_state_is_explicit()
    {
        var adapter = new UnavailableFluidCloudAdapter();
        Assert.Equal(FluidCloudAvailability.Blocked, adapter.Status.Availability);
        Assert.False(adapter.Status.IsAvailable);
        Assert.Contains("不可用", adapter.Status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_projection_renders_an_empty_card()
    {
        var model = CardModelBuilder.Ready(new ProjectionCounts(0, 0, 0), [], FluidCloudStatus.Available());
        Assert.Equal(CardState.Empty, model.State);
        Assert.False(model.HasTasks);
    }
}