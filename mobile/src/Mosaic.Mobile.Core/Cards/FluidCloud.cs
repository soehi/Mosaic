namespace Mosaic.Mobile.Core.Cards;

public enum FluidCloudAvailability
{
    /// <summary>A vendor adapter is installed and the device supports it.</summary>
    Available = 0,

    /// <summary>The platform itself has no live-card surface on this device.</summary>
    Unsupported,

    /// <summary>The platform exists but credentials or vendor access are missing.</summary>
    Blocked,
}

/// <summary>
/// Explicit state of the system-level live card. The AppWidget is never used to
/// stand in for the fluid cloud: when this state is not
/// <see cref="FluidCloudAvailability.Available"/>, no fluid-cloud card exists.
/// </summary>
public sealed record FluidCloudStatus(FluidCloudAvailability Availability, string ReasonCode, string Message)
{
    public static FluidCloudStatus Available() => new(FluidCloudAvailability.Available, "ok", string.Empty);

    public static FluidCloudStatus Unsupported(string reasonCode) =>
        new(FluidCloudAvailability.Unsupported, reasonCode, "流体云不可用：当前系统没有该形态。");

    public static FluidCloudStatus Blocked(string reasonCode) =>
        new(FluidCloudAvailability.Blocked, reasonCode, "流体云不可用：未接入 OPPO 开放平台。");

    public bool IsAvailable => Availability == FluidCloudAvailability.Available;
}

/// <summary>
/// The vendor side of the live card. The Android host binds the unavailable
/// implementation because no OPPO ColorOS credentials or eligible device were
/// available; the boundary stays so a future vendor adapter can be dropped in
/// without touching the kernel.
/// </summary>
public interface IFluidCloudAdapter
{
    FluidCloudStatus Status { get; }

    Task PublishAsync(CardModel model, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

public sealed class UnavailableFluidCloudAdapter : IFluidCloudAdapter
{
    private readonly FluidCloudStatus _status;

    public UnavailableFluidCloudAdapter(FluidCloudStatus? status = null)
    {
        _status = status ?? FluidCloudStatus.Blocked("fluidcloud.vendor_credentials_missing");
    }

    public FluidCloudStatus Status => _status;

    public Task PublishAsync(CardModel model, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}