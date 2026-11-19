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
public sealed record CompanionViewOptions(string Filter, int MaxItems, string OpenColor, string CompletedColor,
    int RowGap = 0, int RowPaddingY = 12, int TitleFontSize = 14, int TitleMaxLines = 2, bool ShowStatusText = true, bool ShowCompletionToggle = true, bool ShowFilterSwitch = true);
public interface ICompanionViewFactory
{
    ICompanionViewSession Create(CompanionViewOptions options);
}
public interface ICompanionViewSession : IAsyncDisposable
{
    FrameworkElement View { get; }
    event EventHandler? LayoutInvalidated { add { } remove { } }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task CloseAsync();
}
public interface INavigationGuard { Task<bool> CanLeaveAsync(); }
public interface IPageController : INavigationGuard, IAsyncDisposable { Task InitializeAsync(); }

// A module-owned entry for the window status bar. The shell renders it generically and never
// looks at what it means; the owning module updates the observable text itself.
public interface IStatusContribution : System.ComponentModel.INotifyPropertyChanged
{
    string Id { get; }
    string Glyph { get; }
    string Text { get; }
    string? Detail { get; }
    string? ToolTip { get; }
    string? TargetViewId { get; }
    int Order { get; }
}

public interface IUiModule
{
    IReadOnlyList<NavigationItem> Navigation { get; }
    IReadOnlyList<ViewContribution> Views { get; }
    IReadOnlyList<IStatusContribution> Status => [];
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

public interface ICompanionNavigation { bool TryGoBack(); }
