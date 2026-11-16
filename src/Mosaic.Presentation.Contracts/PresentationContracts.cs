using System.Windows;

namespace Mosaic.Presentation.Contracts;

public sealed record NavigationItem(
    string Id,
    string Label,
    string IconKey,
    string ViewId,
    int Order,
    string? LabelKey = null);
public sealed record ViewContribution(string Id, IViewFactory Factory);
public sealed record ViewPage(FrameworkElement View, IPageController Controller);
public interface IViewFactory { ViewPage Create(); }
public sealed record CompanionViewOptions(string Filter, int MaxItems, string OpenColor, string CompletedColor);
public interface ICompanionViewFactory
{
    ICompanionViewSession Create(CompanionViewOptions options);
}
public interface ICompanionViewSession : IAsyncDisposable
{
    FrameworkElement View { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task CloseAsync();
}
public interface INavigationGuard { Task<bool> CanLeaveAsync(); }
public interface IPageController : INavigationGuard, IAsyncDisposable { Task InitializeAsync(); }
public interface IUiModule
{
    IReadOnlyList<NavigationItem> Navigation { get; }
    IReadOnlyList<ViewContribution> Views { get; }
}
public interface IUiDispatcher
{
    bool CheckAccess();
    Task InvokeAsync(Action action);
}
public enum UnsavedChoice { Save, Discard, Cancel }
public interface IUserDialogService
{
    Task<bool> ConfirmDeleteAsync(string title);
    Task<bool> ConfirmAsync(string title, string message);
    Task<UnsavedChoice> AskUnsavedChangesAsync();
    Task ShowErrorAsync(string message);
}
