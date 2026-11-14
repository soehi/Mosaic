using Mosaic.Contracts;

namespace Mosaic.Kernel;

public static class ModuleGraph
{
    public static IReadOnlyList<ModuleManifest> Resolve(IReadOnlyList<ModuleManifest> modules, IReadOnlyDictionary<string, string> providers)
    {
        var byId = new Dictionary<string, ModuleManifest>(StringComparer.Ordinal);
        var capabilities = new Dictionary<CapabilityId, string>();
        foreach (var module in modules)
        {
            if (string.IsNullOrWhiteSpace(module.Id) || !byId.TryAdd(module.Id, module)) throw new InvalidOperationException("Duplicate or invalid module ID.");
            if (string.IsNullOrWhiteSpace(module.Responsibility) || !Enum.IsDefined(module.Lifecycle) || module.EntryVersion < 1)
                throw new InvalidOperationException($"Module metadata is invalid: {module.Id}");
            foreach (var capability in module.Provides)
            {
                Validate(capability);
                if (!capabilities.TryAdd(capability, module.Id)) throw new InvalidOperationException($"Duplicate provider: {capability}");
                if (!providers.TryGetValue(capability.ToString(), out var selected) || selected != module.Id)
                    throw new InvalidOperationException($"Provider binding is missing or incompatible: {capability}");
            }
        }
        var result = new List<ModuleManifest>();
        var states = new Dictionary<string, int>();
        void Visit(ModuleManifest module)
        {
            if (states.TryGetValue(module.Id, out var state))
            {
                if (state == 1) throw new InvalidOperationException("Module dependency cycle.");
                return;
            }
            states[module.Id] = 1;
            foreach (var required in module.Requires)
            {
                Validate(required);
                if (!capabilities.TryGetValue(required, out var id)) throw new InvalidOperationException($"Missing capability or incompatible version: {required}");
                Visit(byId[id]);
            }
            states[module.Id] = 2;
            result.Add(module);
        }
        foreach (var module in modules) Visit(module);
        return result.AsReadOnly();
    }
    private static void Validate(CapabilityId id)
    {
        if (string.IsNullOrWhiteSpace(id.Name) || id.Name.Contains('@') || id.MajorVersion < 1) throw new InvalidOperationException("Invalid capability ID.");
    }
}
