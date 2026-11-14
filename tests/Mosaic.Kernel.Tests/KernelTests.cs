using Mosaic.Contracts;
using Mosaic.Kernel;

namespace Mosaic.Kernel.Tests;

public sealed class KernelTests
{
    [Fact]
    public async Task Stopping_rejects_new_requests_and_waits_for_accepted_lease()
    {
        var gate = new ModuleExecutionGate();
        Assert.Null(gate.TryEnter());
        gate.Activate();
        var lease = gate.TryEnter();
        Assert.NotNull(lease);
        var stop = gate.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.Null(gate.TryEnter());
        lease.Dispose(); lease.Dispose();
        await stop;
    }
    [Fact]
    public void Graph_sorts_dependencies_and_rejects_missing_duplicate_cycle_and_versions()
    {
        var a = new ModuleManifest("a", [new("one", 1)], []);
        var b = new ModuleManifest("b", [new("two", 1)], [new("one", 1)]);
        var providers = new Dictionary<string, string> { ["one@1"] = "a", ["two@1"] = "b" };
        Assert.Equal(new[] { "a", "b" }, ModuleGraph.Resolve([b, a], providers).Select(m => m.Id));
        Assert.Throws<InvalidOperationException>(() => ModuleGraph.Resolve([b], providers));
        Assert.Throws<InvalidOperationException>(() => ModuleGraph.Resolve([a, a], providers));
        Assert.Throws<InvalidOperationException>(() => ModuleGraph.Resolve([a, b with { Requires = [new("one", 2)] }], providers));
        Assert.Throws<InvalidOperationException>(() => ModuleGraph.Resolve([a with { Requires = [new("two", 1)] }, b], providers));
        Assert.Throws<InvalidOperationException>(() => ModuleGraph.Resolve([a, b with { Provides = [new("one", 1)] }], providers));
        Assert.Throws<InvalidOperationException>(() => ModuleGraph.Resolve([a with { Responsibility = " " }], providers));
        Assert.Throws<InvalidOperationException>(() => ModuleGraph.Resolve([a with { EntryVersion = 0 }], providers));
    }
    [Fact]
    public async Task Failed_activation_disposes_in_reverse_including_partially_started_module()
    {
        var calls = new List<string>();
        await using var runtime = new ModuleRuntime();
        var a = new ModuleManifest("a", [], []);
        var b = new ModuleManifest("b", [], []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync([a, b], m => new FakeModule(m, calls)));
        Assert.Equal(new[] { "start a", "start b", "stop b", "dispose b", "stop a", "dispose a" }, calls);
    }
    [Fact]
    public async Task Lifecycle_events_announce_start_after_activation_and_stop_before_each_module()
    {
        var calls = new List<string>();
        var runtime = new ModuleRuntime();
        runtime.ModuleStarted += module => calls.Add("started " + module.Manifest.Id);
        runtime.ModuleStopping += module => calls.Add("stopping " + module.Manifest.Id);
        var first = new ModuleManifest("first", [], []);
        var second = new ModuleManifest("second", [], []);

        await runtime.StartAsync([first, second], manifest => new FakeModule(manifest, calls));
        await runtime.StopAsync();

        Assert.Equal(new[] { "start first", "started first", "start second", "started second", "stopping second", "stop second", "dispose second", "stopping first", "stop first", "dispose first" }, calls);
    }
    [Fact]
    public async Task Factory_manifest_mismatch_fails_before_activation_and_disposes_factory_result()
    {
        var calls = new List<string>();
        var requested = new ModuleManifest("a", [], []);
        await using var runtime = new ModuleRuntime();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.StartAsync([requested], _ => new FakeModule(requested with { Id = "b" }, calls)));
        Assert.Contains("does not match", error.Message);
        Assert.Equal(new[] { "dispose b" }, calls);
    }
    private sealed class FakeModule(ModuleManifest manifest, List<string> calls) : IModuleActivation
    {
        public ModuleManifest Manifest => manifest;
        public Task ActivateAsync(CancellationToken cancellationToken = default) { calls.Add("start " + manifest.Id); if (manifest.Id == "b") throw new InvalidOperationException(); return Task.CompletedTask; }
        public Task StopAsync() { calls.Add("stop " + manifest.Id); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { calls.Add("dispose " + manifest.Id); return ValueTask.CompletedTask; }
    }
}
