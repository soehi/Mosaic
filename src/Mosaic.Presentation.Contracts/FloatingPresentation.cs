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
    Task FlushAsync();
}
public interface ICompanionInteraction
{
    bool IsInteracting {get;}
    event EventHandler? InteractionChanged;
    bool CancelInteraction();
}
