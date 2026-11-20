using Mosaic.Contracts;

namespace Mosaic.Kernel;

public sealed class ModuleRuntime : IAsyncDisposable
{
    private readonly List<IModuleActivation> _started = [];
    private bool _stopped;
    private readonly Dictionary<string, IModuleActivation> _activeModules = new(StringComparer.Ordinal);

    public T Resolve<T>(CapabilityId capability, IReadOnlyDictionary<string, string> bindings) where T : class
    {
        var key = capability.ToString();
        if (!bindings.TryGetValue(key, out var providerId) || !_activeModules.TryGetValue(providerId, out var module))
            throw new InvalidOperationException($"Capability '{key}' has no active bound provider.");
        if (!module.Manifest.Provides.Contains(capability) || module is not ICapabilityProvider provider || provider.GetCapability(capability) is not T value)
            throw new InvalidOperationException($"Provider '{providerId}' does not export capability '{key}' as {typeof(T).Name}.");
        return value;
    }
    public event Action<IModuleActivation>? ModuleStarted;
    public event Action<IModuleActivation>? ModuleStopping;

    public Task StartAsync(IReadOnlyList<ModuleManifest> ordered, Func<ModuleManifest, IModuleActivation> factory, CancellationToken cancellationToken = default) =>
        StartAsync(ordered, factory, cancellationToken, null);

    public async Task StartAsync(IReadOnlyList<ModuleManifest> ordered, Func<ModuleManifest, IModuleActivation> factory, CancellationToken cancellationToken, Func<IModuleActivation, CancellationToken, Task>? beforeActivate)
    {
        if (_started.Count > 0 || _stopped) throw new InvalidOperationException("Runtime is single use.");
        try
        {
            foreach (var manifest in ordered)
            {
                var module = factory(manifest) ?? throw new InvalidOperationException($"Factory returned no module for manifest '{manifest.Id}'.");
                if (!Matches(manifest, module.Manifest))
                {
                    try { await module.DisposeAsync(); } catch { /* Preserve the assembly mismatch error. */ }
                    throw new InvalidOperationException($"Factory returned a module that does not match manifest '{manifest.Id}'.");
                }
                _started.Add(module); // Include partial activation in rollback.
                if (beforeActivate is not null) await beforeActivate(module, cancellationToken);
                await module.ActivateAsync(cancellationToken);
                _activeModules.Add(manifest.Id, module);
                ModuleStarted?.Invoke(module);
            }
        }
        catch
        {
            try { await DisposeAsync(); } catch { /* Preserve the activation error. */ }
            throw;
        }
    }
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        var errors = new List<Exception>();
        foreach (var module in _started.AsEnumerable().Reverse())
        {
            _activeModules.Remove(module.Manifest.Id);
            try { ModuleStopping?.Invoke(module); } catch (Exception ex) { errors.Add(ex); }
            try { await module.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await module.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        }
        _started.Clear();
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    public async ValueTask DisposeAsync() => await StopAsync();

    private static bool Matches(ModuleManifest expected, ModuleManifest? actual) =>
        actual is not null &&
        actual.Id == expected.Id &&
        actual.Responsibility == expected.Responsibility &&
        actual.Lifecycle == expected.Lifecycle &&
        actual.EntryVersion == expected.EntryVersion &&
        actual.Provides.Count == expected.Provides.Count &&
        actual.Provides.ToHashSet().SetEquals(expected.Provides) &&
        actual.Requires.Count == expected.Requires.Count &&
        actual.Requires.ToHashSet().SetEquals(expected.Requires);
}
