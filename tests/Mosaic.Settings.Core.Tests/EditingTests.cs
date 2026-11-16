using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Core;

namespace Mosaic.Settings.Core.Tests;

public sealed class EditingTests
{
    private sealed class Repository : ISettingRepository
    {
        internal Dictionary<string, object?> Values = [];
        internal int Writes;
        internal bool ThrowSave;
        public Task<OperationResult<IReadOnlyDictionary<string, object?>>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, object?>>.Success(new Dictionary<string, object?>(Values)));
        public Task<OperationResult> SaveAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
        {
            if (ThrowSave) throw new OperationCanceledException();
            Writes++; foreach (var item in values) Values[item.Key] = item.Value;
            return Task.FromResult(OperationResult.Success());
        }
        public Task<OperationResult> DeleteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private static SettingDefinition Define(string id, SettingPreviewMode preview = SettingPreviewMode.Reversible,
        SettingApplyMode mode = SettingApplyMode.Immediate, SettingPersistence persistence = SettingPersistence.UserSettings) =>
        new(id, "alpha", id, id, SettingValueKind.Integer, 1, 0, 100, null, SettingsGroup.Appearance,
            SettingCategory.Normal, mode, persistence, PreviewMode: preview);
    private sealed class Source : ISettingSource
    {
        internal Dictionary<string, object?> Latest = [];
        internal Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task>? Apply;
        public string ModuleId => "alpha";
        public IReadOnlyList<SettingDefinition> Definitions { get; } =
            [Define("alpha.preview"), Define("alpha.none", SettingPreviewMode.None), Define("alpha.restart", mode: SettingApplyMode.Restart)];
        internal int Calls;
        public async Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
        {
            Calls++; Latest = new(values);
            if (Apply is not null) await Apply(values, cancellationToken);
        }
    }
    private sealed class Host : IHostSettingSource
    {
        internal int Writes, Rollbacks;
        internal object Value = 1;
        private object? _saved;
        public string ModuleId => "host";
        public IReadOnlyList<SettingDefinition> Definitions { get; } = [Define("host.value", mode: SettingApplyMode.Restart, persistence: SettingPersistence.HostConfig) with { ModuleId = "host" }];
        public object? GetValue(string id) => Value;
        public Task<OperationResult> ValidateAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PersistAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
        { _saved = Value; Value = values["host.value"]!; Writes++; return Task.FromResult(OperationResult.Success()); }
        public Task<OperationResult> RollbackAsync(CancellationToken cancellationToken = default)
        { Assert.False(cancellationToken.IsCancellationRequested); Value = _saved!; Rollbacks++; return Task.FromResult(OperationResult.Success()); }
        public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Host configuration cannot preview.");
    }

    [Fact]
    public async Task Preview_never_persists_and_applies_only_reversible_immediate_values()
    {
        var repository = new Repository(); var host = new Host(); var source = new Source();
        await using var module = new SettingsModule(new(repository, host));
        using var registration = module.Api.Register(source);
        await using var session = (await module.Api.BeginEditSessionAsync()).Value;
        var result = await session.PreviewAsync(new Dictionary<string, object?>
        { ["alpha.preview"] = 2, ["alpha.none"] = 3, ["alpha.restart"] = 4, ["host.value"] = 5 }, 1);
        Assert.Empty(result.Value.Errors);
        Assert.Equal(2, source.Latest["alpha.preview"]); Assert.Equal(1, source.Latest["alpha.none"]);
        Assert.DoesNotContain("alpha.restart", source.Latest.Keys); Assert.DoesNotContain("host.value", source.Latest.Keys);
        Assert.Single(result.Value.AppliedDefinitionIds); Assert.Equal(3, result.Value.SaveOnlyDefinitionIds.Count);
        Assert.Equal(0, repository.Writes); Assert.Equal(0, host.Writes);
        Assert.Equal(1, (await module.Api.GetValuesAsync()).Value["alpha.preview"]);
        Assert.True((await session.DiscardAsync()).IsSuccess); Assert.Equal(1, source.Latest["alpha.preview"]);
    }

    [Fact]
    public async Task One_session_blocks_other_writers_and_releases_on_dispose()
    {
        await using var module = new SettingsModule(new(new Repository())); using var registration = module.Api.Register(new Source());
        var session = (await module.Api.BeginEditSessionAsync()).Value;
        Assert.Equal(ErrorCodes.Conflict, (await module.Api.BeginEditSessionAsync()).Error!.Code);
        Assert.Equal(ErrorCodes.Conflict, (await module.Api.ProposeChangesAsync(new Dictionary<string, object?>())).Error!.Code);
        Assert.Equal(ErrorCodes.Conflict, (await module.Api.ResetAsync()).Error!.Code);
        await session.DisposeAsync();
        await using var next = (await module.Api.BeginEditSessionAsync()).Value;
    }

    [Fact]
    public async Task Invalid_candidate_keeps_last_preview_and_empty_overlay_restores_saved_baseline()
    {
        var source = new Source(); await using var module = new SettingsModule(new(new Repository())); using var registration = module.Api.Register(source);
        await using var session = (await module.Api.BeginEditSessionAsync()).Value;
        await session.PreviewAsync(new Dictionary<string, object?> { ["alpha.preview"] = 2 }, 1);
        var calls = source.Calls;
        var invalid = await session.PreviewAsync(new Dictionary<string, object?> { ["alpha.preview"] = "invalid" }, 2);
        Assert.Contains("alpha.preview", invalid.Value.Errors.Keys); Assert.Equal(calls, source.Calls);
        Assert.Equal(2, source.Latest["alpha.preview"]);
        Assert.False((await session.CommitAsync(2)).IsSuccess);
        await session.PreviewAsync(new Dictionary<string, object?>(), 3);
        Assert.Equal(1, source.Latest["alpha.preview"]);
    }

    [Fact]
    public async Task Commit_updates_baseline_and_next_batch_rolls_back_to_new_saved_value()
    {
        var repository = new Repository(); var source = new Source();
        await using var module = new SettingsModule(new(repository)); using var registration = module.Api.Register(source);
        var session = (await module.Api.BeginEditSessionAsync()).Value;
        await session.PreviewAsync(new Dictionary<string, object?> { ["alpha.preview"] = 2, ["alpha.none"] = 3, ["alpha.restart"] = 4 }, 1);
        Assert.True((await session.CommitAsync(1)).IsSuccess);
        Assert.Equal(1, repository.Writes); Assert.Equal(2, session.SavedValues["alpha.preview"]);
        Assert.Equal(3, source.Latest["alpha.none"]); Assert.DoesNotContain("alpha.restart", source.Latest.Keys);
        await session.PreviewAsync(new Dictionary<string, object?> { ["alpha.preview"] = 6 }, 2);
        await session.DisposeAsync();
        Assert.Equal(2, source.Latest["alpha.preview"]); Assert.Equal(3, source.Latest["alpha.none"]);
        Assert.Equal(2, (await module.Api.GetValuesAsync()).Value["alpha.preview"]);
    }

    [Fact]
    public async Task A_partially_failing_source_is_included_in_compensation()
    {
        var source = new Source { Apply = (values, _) => Equals(values["alpha.preview"], 2) ? Task.FromException(new IOException()) : Task.CompletedTask };
        await using var module = new SettingsModule(new(new Repository())); using var registration = module.Api.Register(source);
        await using var session = (await module.Api.BeginEditSessionAsync()).Value;
        var preview = await session.PreviewAsync(new Dictionary<string, object?> { ["alpha.preview"] = 2 }, 1);
        Assert.Contains("settings.preview", preview.Value.Errors.Keys); Assert.Equal(2, source.Calls);
        Assert.Equal(1, source.Latest["alpha.preview"]);
    }

    [Fact]
    public async Task Discard_failure_stays_unconfirmed_and_can_be_retried()
    {
        var source = new Source(); await using var module = new SettingsModule(new(new Repository())); using var registration = module.Api.Register(source);
        await using var session = (await module.Api.BeginEditSessionAsync()).Value;
        await session.PreviewAsync(new Dictionary<string, object?> { ["alpha.preview"] = 2 }, 1);
        source.Apply = (values, _) => Equals(values["alpha.preview"], 1) ? Task.FromException(new IOException()) : Task.CompletedTask;
        Assert.False((await session.DiscardAsync()).IsSuccess);
        source.Apply = null;
        Assert.True((await session.DiscardAsync()).IsSuccess); Assert.Equal(1, source.Latest["alpha.preview"]);
    }

    [Fact]
    public async Task Concurrent_previews_serialize_and_reject_stale_revisions()
    {
        var gate = new TaskCompletionSource(); var entered = new TaskCompletionSource();
        var source = new Source { Apply = async (values, token) => { if (Equals(values["alpha.preview"], 2)) { entered.TrySetResult(); await gate.Task.WaitAsync(token); } } };
        await using var module = new SettingsModule(new(new Repository())); using var registration = module.Api.Register(source);
        await using var session = (await module.Api.BeginEditSessionAsync()).Value;
        var first = session.PreviewAsync(new Dictionary<string, object?> { ["alpha.preview"] = 2 }, 1);
        await entered.Task;
        var second = session.PreviewAsync(new Dictionary<string, object?> { ["alpha.preview"] = 3 }, 2);
        Assert.False(second.IsCompleted); gate.SetResult(); await Task.WhenAll(first, second);
        Assert.Equal(3, source.Latest["alpha.preview"]);
        Assert.Equal(ErrorCodes.Conflict, (await session.PreviewAsync(new Dictionary<string, object?>(), 1)).Error!.Code);
        Assert.False((await session.CommitAsync(1)).IsSuccess);
    }

    [Fact]
    public async Task Source_stop_rolls_back_before_unregistering_and_removes_session_values()
    {
        var source = new Source(); await using var module = new SettingsModule(new(new Repository()));
        using var registration = module.Api.Register(source);
        await using var session = (await module.Api.BeginEditSessionAsync()).Value;
        await session.PreviewAsync(new Dictionary<string, object?> { ["alpha.preview"] = 2 }, 1);
        Assert.Throws<InvalidOperationException>(() => registration.Dispose());
        Assert.True((await module.Api.PrepareSourceStopAsync("alpha")).IsSuccess);
        Assert.Equal(1, source.Latest["alpha.preview"]); Assert.Empty(session.SavedValues);
        registration.Dispose(); Assert.Empty((await module.Api.ListDefinitionsAsync()).Value);
    }

    [Fact]
    public async Task Host_write_is_compensated_when_repository_throws_or_cancels()
    {
        var host = new Host(); var repository = new Repository { ThrowSave = true };
        await using var module = new SettingsModule(new(repository, host)); using var registration = module.Api.Register(new Source());
        await using var session = (await module.Api.BeginEditSessionAsync()).Value;
        await session.PreviewAsync(new Dictionary<string, object?> { ["alpha.preview"] = 2, ["host.value"] = 3 }, 1);
        Assert.False((await session.CommitAsync(1)).IsSuccess);
        Assert.Equal(1, host.Rollbacks); Assert.Equal(1, host.Value); Assert.Equal(0, repository.Writes);
        Assert.Equal(1, session.SavedValues["alpha.preview"]);
    }

    [Fact]
    public async Task Apply_warning_is_a_successful_commit_and_advances_saved_baseline()
    {
        var source = new Source(); var repository = new Repository();
        await using var module = new SettingsModule(new(repository)); using var registration = module.Api.Register(source);
        await using var session = (await module.Api.BeginEditSessionAsync()).Value;
        await session.PreviewAsync(new Dictionary<string, object?> { ["alpha.preview"] = 2 }, 1);
        source.Apply = (_, _) => Task.FromException(new IOException());
        var commit = await session.CommitAsync(1);
        Assert.True(commit.IsSuccess); Assert.Contains(SettingResults.ApplyWarningId, commit.Value.ApplyWarnings.Keys);
        Assert.Equal(2, session.SavedValues["alpha.preview"]); Assert.Equal(1, repository.Writes);
        source.Apply = null;
    }
}
