namespace Mosaic.Contracts;

public readonly record struct CapabilityId(string Name, int MajorVersion)
{
    public override string ToString() => $"{Name}@{MajorVersion}";
}

public enum ModuleLifecycle { ActivateStopDispose }

public sealed record ModuleManifest(
    string Id,
    IReadOnlyList<CapabilityId> Provides,
    IReadOnlyList<CapabilityId> Requires,
    string Responsibility = "unspecified",
    ModuleLifecycle Lifecycle = ModuleLifecycle.ActivateStopDispose,
    int EntryVersion = 1);

public interface IModuleActivation : IAsyncDisposable
{
    ModuleManifest Manifest { get; }
    Task ActivateAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface IModuleExecutionGate
{
    void Activate();
    IDisposable? TryEnter();
    Task StopAsync();
}

public interface IIdGenerator
{
    Guid NewId();
}

public interface IDiagnosticSink
{
    void Report(string correlationId, Exception exception);
}
