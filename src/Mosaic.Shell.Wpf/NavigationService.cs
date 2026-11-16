using Mosaic.Presentation.Contracts;

namespace Mosaic.Shell.Wpf;

internal sealed class NavigationService(ContributionRegistry registry, IUserDialogService dialogs) : IAsyncDisposable
{
    internal ViewPage? Current { get; private set; }
    internal string? CurrentViewId { get; private set; }
    internal async Task<bool> NavigateAsync(string viewId)
    {
        if (CurrentViewId == viewId) return true;
        var factory = registry.Find(viewId);
        if (factory is null) { await dialogs.ShowErrorAsync("该页面暂不可用。"); return false; }
        if (Current is not null && !await Current.Controller.CanLeaveAsync()) return false;
        var next = factory.Create();
        try { await next.Controller.InitializeAsync(); }
        catch { await next.Controller.DisposeAsync(); throw; }
        try { await ReleaseCurrentAsync(); }
        catch { await next.Controller.DisposeAsync(); throw; }
        Current = next;
        CurrentViewId = viewId;
        return true;
    }
    internal ValueTask ReleaseCurrentAsync()
    {
        var current = Current;
        Current = null;
        CurrentViewId = null;
        return current is null ? ValueTask.CompletedTask : current.Controller.DisposeAsync();
    }
    public ValueTask DisposeAsync() => ReleaseCurrentAsync();
}
