using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.App.FluidCloud;

/// <summary>
/// Fluid-cloud boundary for Android. Stage 0 of the refactor plan concluded
/// that OPPO ColorOS 流体云 cannot be integrated in this delivery: no OPPO
/// open-platform credentials (ClientId/ClientSecret) were available, the vendor
/// documentation is permission-gated, and no ColorOS 15+ device was attached.
/// The claim that ColorOS 16 accepts plain Android 16 Live Updates could not be
/// verified against official interfaces.
///
/// Therefore this adapter reports an explicit blocked state and publishes
/// nothing. It never renders the AppWidget as if it were the fluid cloud.
/// </summary>
public sealed class AndroidFluidCloudAdapter : IFluidCloudAdapter
{
    public static readonly FluidCloudStatus StatusValue =
        FluidCloudStatus.Blocked("fluidcloud.oppo_credentials_missing");

    public FluidCloudStatus Status => StatusValue;

    public Task PublishAsync(CardModel model, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}