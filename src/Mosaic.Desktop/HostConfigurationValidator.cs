using Mosaic.Contracts;
using Mosaic.Kernel;
using Mosaic.Settings.Contracts;

namespace Mosaic.Desktop;

internal static class HostConfigurationValidator
{
    internal static OperationResult ValidateDeclarations(
        IReadOnlyList<string> modules,
        IReadOnlyDictionary<string, string> providers)
    {
        if (modules.Count != modules.Distinct(StringComparer.Ordinal).Count() ||
            modules.Any(string.IsNullOrWhiteSpace) ||
            modules.Any(id => !BuiltInModuleCatalog.Definitions.ContainsKey(id)))
        {
            return OperationResult.Failure(new(
                ErrorCodes.ValidationFailed,
                "模块列表包含未知或重复的模块。",
                "host.modules"));
        }

        var stale = providers
            .Where(item =>
                string.IsNullOrWhiteSpace(item.Key) ||
                string.IsNullOrWhiteSpace(item.Value) ||
                !BuiltInModuleCatalog.Definitions.TryGetValue(item.Value, out var definition) ||
                !modules.Contains(item.Value, StringComparer.Ordinal) ||
                !definition.Provides.Any(capability => capability.ToString() == item.Key))
            .Select(item => item.Key)
            .ToArray();
        if (stale.Length > 0)
        {
            return OperationResult.Failure(new(
                ErrorCodes.ValidationFailed,
                "能力绑定无效：" + string.Join("、", stale),
                "host.providers"));
        }

        return OperationResult.Success();
    }

    internal static OperationResult Validate(
        IReadOnlyList<string> modules,
        IReadOnlyDictionary<string, string> providers)
    {
        var declarations = ValidateDeclarations(modules, providers);
        if (!declarations.IsSuccess) return declarations;

        try
        {
            _ = ModuleGraph.Resolve(
                modules.Select(id => BuiltInModuleCatalog.Definitions[id]).ToArray(),
                providers);
            return OperationResult.Success();
        }
        catch (InvalidOperationException exception)
        {
            return OperationResult.Failure(new(
                ErrorCodes.ValidationFailed,
                "模块依赖或能力绑定无效：" + exception.Message,
                "host.modules"));
        }
    }
}
