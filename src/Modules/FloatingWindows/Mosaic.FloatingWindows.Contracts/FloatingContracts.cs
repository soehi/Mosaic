using Mosaic.Contracts;
namespace Mosaic.FloatingWindows.Contracts;

public sealed record FloatingContentReference(string ProviderId, string ItemKey);
// Physical screen point. Persisted dimensions/offsets below are DIP, never screen pixels.
public sealed record FloatingPlacementHint(double ScreenX, double ScreenY, double GrabX = 0, double GrabY = 0);
public enum FloatingDisplayMode { Compact, Expanded }
public enum FloatingOpenDisposition { Created, ActivatedExisting }
public sealed record FloatingOpenResult(FloatingOpenDisposition Disposition);
public sealed record FloatingWindowRecord(FloatingContentReference Reference, bool IsOpen, FloatingDisplayMode Mode,
    double CompactWidth, double ExpandedWidth, double ExpandedHeight, string MonitorId, double X, double Y,
    bool IsTopmost, long Revision = 0, int SchemaVersion = 1);
public interface IFloatingWindowApi
{
    Task<OperationResult<FloatingOpenResult>> OpenAsync(FloatingContentReference reference, FloatingPlacementHint? hint = null, CancellationToken cancellationToken = default);
    Task<OperationResult> CloseAsync(FloatingContentReference reference, CancellationToken cancellationToken = default);
    // UI-thread placement ghost. The hint uses the same math as OpenAsync so preview equals the eventual window.
    void BeginPlacementPreview(FloatingContentReference reference, FloatingPlacementHint hint);
    void UpdatePlacementPreview(FloatingPlacementHint hint);
    void EndPlacementPreview();
}
public interface IFloatingWindowSurface
{
    FloatingWindowRecord CreateRecord(FloatingContentReference reference, FloatingPlacementHint? hint);
    Task<OperationResult> ShowAsync(FloatingWindowRecord record, CancellationToken cancellationToken);
    void Activate(FloatingContentReference reference);
    Task DestroyAsync(FloatingContentReference reference);
    FloatingWindowRecord Snapshot(FloatingWindowRecord record);
    void Apply(FloatingWindowRecord record);
    void BeginPreview(FloatingContentReference reference, FloatingPlacementHint hint);
    void UpdatePreview(FloatingPlacementHint hint);
    void EndPreview();
}
