using System.Windows;
using Mosaic.Contracts;
using Mosaic.FloatingWindows.Contracts;
namespace Mosaic.Presentation.Contracts;
public interface IFloatingDetachSource
{
    bool IsAvailable {get;}
    event EventHandler? AvailabilityChanged;
    IDisposable RegisterDetachHandler(IFloatingWindowApi handler);
    Task<OperationResult<FloatingOpenResult>> RequestDetachAsync(FloatingContentReference reference,FloatingPlacementHint? hint=null,CancellationToken cancellationToken=default);
    void BeginPlacementPreview(FloatingContentReference reference,FloatingPlacementHint hint);
    void UpdatePlacementPreview(FloatingPlacementHint hint);
    void EndPlacementPreview();
}
public interface IFloatingContentFactory { IFloatingContentSession Create(FloatingContentReference reference); }
public sealed record FloatingContentLayoutOptions(double ContentGap, double HeaderTrailingSpace);
public interface IFloatingContentSession : IAsyncDisposable
{
    FrameworkElement View {get;}
    FrameworkElement? Header => null;
    void ConfigureLayout(FloatingContentLayoutOptions options) { }
    event EventHandler? LayoutInvalidated;
    event EventHandler? Missing;
    event EventHandler? ModeRequested;
    Task<OperationResult> InitializeAsync(CancellationToken cancellationToken=default);
    void SetExpanded(bool expanded);
    bool HandleEscape() => false;
    // A successful preparation reserves the current draft until disposal or cancellation.
    Task<OperationResult> PrepareCloseAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());
    void CancelClosePreparation() { }
    Task FlushAsync();
}
public interface ICompanionInteraction
{
    bool IsInteracting {get;}
    event EventHandler? InteractionChanged;
    bool CancelInteraction();
}
