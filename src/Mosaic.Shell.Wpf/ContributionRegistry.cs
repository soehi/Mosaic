using System.Collections.ObjectModel;
using Mosaic.Presentation.Contracts;

namespace Mosaic.Shell.Wpf;

public sealed class ContributionRegistry
{
    private readonly Dictionary<string, ViewContribution> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NavigationItem> _navigation = new(StringComparer.Ordinal);
    private readonly HashSet<Registration> _registrations = [];
    private readonly ObservableCollection<NavigationItem> _visibleNavigation = [];

    public ContributionRegistry() => Navigation = new ReadOnlyObservableCollection<NavigationItem>(_visibleNavigation);

    public IReadOnlyList<NavigationItem> Navigation { get; }

    internal event EventHandler? Changed;

    public IDisposable Register(IReadOnlyList<NavigationItem> navigation, IReadOnlyList<ViewContribution> views)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(views);

        var allViews = new Dictionary<string, ViewContribution>(_views, StringComparer.Ordinal);
        var allNavigation = new Dictionary<string, NavigationItem>(_navigation, StringComparer.Ordinal);
        foreach (var view in views)
            if (string.IsNullOrWhiteSpace(view.Id) || view.Factory is null || !allViews.TryAdd(view.Id, view))
                throw new InvalidOperationException("Duplicate or invalid view contribution.");
        foreach (var item in navigation)
            if (string.IsNullOrWhiteSpace(item.Id) || !allNavigation.TryAdd(item.Id, item) || !allViews.ContainsKey(item.ViewId))
                throw new InvalidOperationException("Duplicate navigation or missing view contribution.");

        var registration = new Registration(
            views.Select(view => view.Id).ToArray(),
            navigation.Select(item => item.Id).ToArray());
        foreach (var view in views) _views.Add(view.Id, view);
        foreach (var item in navigation) _navigation.Add(item.Id, item);
        _registrations.Add(registration);
        RebuildNavigation();
        return new RegistrationHandle(this, registration);
    }

    internal IViewFactory? Find(string viewId) => _views.GetValueOrDefault(viewId)?.Factory;

    public void Clear()
    {
        _registrations.Clear();
        _navigation.Clear();
        _views.Clear();
        RebuildNavigation();
    }

    private void Revoke(Registration registration)
    {
        if (!_registrations.Remove(registration)) return;
        foreach (var id in registration.ViewIds) _views.Remove(id);
        foreach (var id in registration.NavigationIds) _navigation.Remove(id);
        RebuildNavigation();
    }

    private void RebuildNavigation()
    {
        _visibleNavigation.Clear();
        foreach (var item in _navigation.Values.OrderBy(item => item.Order).ThenBy(item => item.Id, StringComparer.Ordinal))
            _visibleNavigation.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Registration(string[] viewIds, string[] navigationIds)
    {
        internal string[] ViewIds { get; } = viewIds;
        internal string[] NavigationIds { get; } = navigationIds;
    }

    private sealed class RegistrationHandle(ContributionRegistry owner, Registration registration) : IDisposable
    {
        private ContributionRegistry? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Revoke(registration);
        }
    }
}
