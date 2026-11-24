using System.Windows;

namespace Mosaic.Presentation.Contracts;

public sealed record NavigationItem(
    string Id,
    string Label,
    string IconKey,
    string ViewId,
    int Order,
    string? LabelKey = null,
    bool IsDefault = false);
public sealed record ViewContribution(string Id, IViewFactory Factory);
public sealed record ViewPage(FrameworkElement View, IPageController Controller);
public sealed record SettingsPageContribution(string GroupId, string Id, IViewFactory Factory);
public interface IViewFactory { ViewPage Create(); }
public interface ISettingsPageModule
{
    IReadOnlyList<SettingsPageContribution> SettingsPages { get; }
}
public sealed record CompanionViewOptions(string Filter, int MaxItems, string OpenColor, string CompletedColor,
    int RowGap = 0, int RowPaddingY = 12, int TitleFontSize = 14, int TitleMaxLines = 2, bool ShowCompletionToggle = true, bool ShowFilterSwitch = true);
public interface ICompanionViewFactory
{
    ICompanionViewSession Create(CompanionViewOptions options);
}
public interface ICompanionViewSession : IAsyncDisposable
{
    FrameworkElement View { get; }
    event EventHandler? LayoutInvalidated { add { } remove { } }
    // Lets the host apply its dismissal policy after a completed interaction.
    event EventHandler? DismissRequested { add { } remove { } }
    // Raised when the user selects a different filter in the companion.
    event EventHandler<string>? FilterChanged { add { } remove { } }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task CloseAsync();
}
// 显式可选能力：宿主优先通过它订阅筛选变化，未实现时回退到 ICompanionViewSession.FilterChanged。
// 基础接口的同名事件保留默认实现，避免要求所有伴随视图实现新增成员。
public interface ICompanionFilterSelection
{
    event EventHandler<string>? FilterChanged;
}
public interface INavigationGuard { Task<bool> CanLeaveAsync(); }
public interface IPageController : INavigationGuard, IAsyncDisposable { Task InitializeAsync(); }

// Where a status contribution may render. The bar is the default; Bottom opts the same
// contribution into the sidebar's bottom area as well, rendered generically by the shell.
public enum StatusPlacement { None, Bottom }

// A module-owned entry for the window status bar. The shell renders it generically and never
// looks at what it means; the owning module updates the observable text itself. Optional
// members keep existing contributors source-compatible without recompilation concerns.
public interface IStatusContribution : System.ComponentModel.INotifyPropertyChanged
{
    string Id { get; }
    string Glyph { get; }
    string Text { get; }
    string? Detail { get; }
    string? ToolTip { get; }
    string? TargetViewId { get; }
    int Order { get; }
    // Optional sidebar placement; None keeps the contribution in the bottom status bar only.
    StatusPlacement SidebarPlacement => StatusPlacement.None;
    // Optional action the shell renders next to the sidebar entry when the module says it is
    // available (e.g. sign-out). The shell never names the action; it forwards the request.
    bool CanAct => false;
    string? ActionGlyph => null;
    string? ActionLabel => null;
    string? ActionToolTip => null;
    event EventHandler? ActionRequested { add { } remove { } }
    void RequestAction() { }
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
