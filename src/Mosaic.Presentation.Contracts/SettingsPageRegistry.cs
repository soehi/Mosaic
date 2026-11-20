namespace Mosaic.Presentation.Contracts;

public sealed class SettingsPageRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SettingsPageContribution> _pages = new(StringComparer.Ordinal);

    public string? InitialGroupId { get; set; }
    public bool IsInitialChoiceActive { get; set; }
    public event EventHandler? InitialChoiceCompleted;
    public event EventHandler? StartPageRequested;
    public void RequestStartPage() { IsInitialChoiceActive = true; StartPageRequested?.Invoke(this, EventArgs.Empty); }
    public void CompleteInitialChoice() => InitialChoiceCompleted?.Invoke(this, EventArgs.Empty);
    public IReadOnlyList<SettingsPageContribution> Pages
    {
        get { lock (_gate) return _pages.Values.OrderBy(page => page.GroupId, StringComparer.Ordinal).ThenBy(page => page.Id, StringComparer.Ordinal).ToArray(); }
    }

    public IDisposable Register(SettingsPageContribution page)
    {
        if (string.IsNullOrWhiteSpace(page.GroupId)) throw new ArgumentException("A settings group id is required.", nameof(page));
        if (string.IsNullOrWhiteSpace(page.Id)) throw new ArgumentException("A settings page id is required.", nameof(page));
        ArgumentNullException.ThrowIfNull(page.Factory);
        lock (_gate)
            if (!_pages.TryAdd(page.Id, page)) throw new InvalidOperationException($"Settings page '{page.Id}' is already registered.");
        return new Registration(this, page.Id);
    }

    private void Remove(string id)
    {
        lock (_gate) _pages.Remove(id);
    }

    private sealed class Registration(SettingsPageRegistry owner, string id) : IDisposable
    {
        private SettingsPageRegistry? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(id);
    }
}
