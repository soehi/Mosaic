using System.Text.Json;
using System.Reflection;
using Mosaic.Contracts;
using Mosaic.ReleaseSummary;
using Mosaic.Settings.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.ReleaseSummary.Tests;

public sealed class ReleaseSummaryTests
{
    private static ReleaseContent Release(string version, string? previous = null, string? note = null)
    {
        var release = new ReleaseContent("mosaic-" + version, version, "2026-11-16",
            previous is null ? null : "mosaic-" + previous, [note ?? "新增 " + version], ["改进 " + version], ["修复 " + version], "");
        return release with { ContentSha256 = ReleaseManifest.ComputeContentSha256(release) };
    }

    private static ReleaseManifest Manifest(params ReleaseContent[] releases) => new(1, releases[^1].ReleaseId, releases);
    private static string Json(ReleaseManifest manifest) => JsonSerializer.Serialize(manifest);

    [Fact]
    public void First_run_includes_current_release_only_and_complete_categories()
    {
        var manifest = Manifest(Release("0.5.1"), Release("0.6.0", "0.5.1"));
        Assert.Null(manifest.Validate("0.6.0+abcdef"));
        var result = ReleaseSummaryComposer.Compose(manifest, []);
        Assert.True(result.IsSuccess);
        Assert.Equal("mosaic-0.6.0", result.Value.ReleaseId);
        Assert.Contains("Mosaic 0.6.0 · 更新日期 2026-11-16", result.Value.Notes);
        Assert.Contains("适用范围：首次使用 → 0.6.0", result.Value.Notes);
        Assert.Contains("新增 0.6.0", result.Value.Notes);
        Assert.Contains("改进 0.6.0", result.Value.Notes);
        Assert.Contains("修复 0.6.0", result.Value.Notes);
        Assert.DoesNotContain("新增 0.5.1", result.Value.Notes);
        Assert.Equal("更新简要", ReleaseSummaryComposer.TaskTitle);
    }

    [Fact]
    public void Upgrade_combines_each_unannounced_release_in_semantic_order()
    {
        var manifest = Manifest(Release("0.8.0"), Release("0.9.0", "0.8.0"), Release("0.10.0", "0.9.0"));
        var result = ReleaseSummaryComposer.Compose(manifest, ["mosaic-0.8.0"]);
        Assert.True(result.IsSuccess);
        Assert.Contains("[0.9.0] 新增 0.9.0", result.Value.Notes);
        Assert.Contains("[0.10.0] 新增 0.10.0", result.Value.Notes);
        Assert.True(result.Value.Notes.IndexOf("[0.9.0]", StringComparison.Ordinal) <
                    result.Value.Notes.IndexOf("[0.10.0]", StringComparison.Ordinal));
        Assert.DoesNotContain("新增 0.8.0", result.Value.Notes);
    }

    [Theory]
    [InlineData("0.10.0")]
    [InlineData("0.11.0")]
    public void Equal_or_higher_announced_version_does_not_generate(string announced)
    {
        var releases = new[] { Release("0.9.0"), Release("0.10.0", "0.9.0"), Release("0.11.0", "0.10.0") };
        var manifest = new ReleaseManifest(1, "mosaic-0.10.0", releases);
        Assert.Empty(ReleaseSummaryComposer.Compose(manifest, ["mosaic-" + announced]).Value.Notes);
    }

    [Fact]
    public void Prerelease_numbers_are_compared_numerically_and_stable_is_newer()
    {
        var manifest = Manifest(Release("1.0.0-rc.2"), Release("1.0.0-rc.10", "1.0.0-rc.2"), Release("1.0.0", "1.0.0-rc.10"));
        var result = ReleaseSummaryComposer.Compose(manifest, ["mosaic-1.0.0-rc.2"]);
        Assert.True(result.IsSuccess);
        Assert.Contains("[1.0.0-rc.10]", result.Value.Notes);
        Assert.Contains("[1.0.0]", result.Value.Notes);
    }

    [Fact]
    public void Unknown_receipt_or_missing_history_does_not_fall_back_to_first_run()
    {
        var manifest = Manifest(Release("0.5.1"), Release("0.6.0"));
        Assert.False(ReleaseSummaryComposer.Compose(manifest, ["unknown-release"]).IsSuccess);
        Assert.False(ReleaseSummaryComposer.Compose(manifest, ["mosaic-0.5.1"]).IsSuccess);
    }

    [Fact]
    public void Empty_intermediate_content_is_not_silently_omitted()
    {
        var missing = new ReleaseContent("mosaic-0.5.2", "0.5.2", "2026-11-16", "mosaic-0.5.1", [], [], [], "");
        missing = missing with { ContentSha256 = ReleaseManifest.ComputeContentSha256(missing) };
        var manifest = Manifest(Release("0.5.1"), missing, Release("0.6.0", "0.5.2"));
        Assert.False(ReleaseSummaryComposer.Compose(manifest, ["mosaic-0.5.1"]).IsSuccess);
    }

    [Fact]
    public void Manifest_rejects_version_drift_tampering_invalid_dates_and_default_limit_overflow()
    {
        var original = Release("0.6.0");
        Assert.False(ReleaseManifest.Parse(Json(Manifest(original)), "0.5.1").IsSuccess);
        Assert.False(ReleaseManifest.Parse(Json(Manifest(original with { Added = ["changed"] })), "0.6.0").IsSuccess);
        Assert.False(ReleaseManifest.Parse(Json(Manifest(original with { ReleasedOn = "2026-99-99" })), "0.6.0").IsSuccess);
        Assert.False(ReleaseManifest.Parse(Json(Manifest(Release("0.6.0", note: new string('文', 20001)))), "0.6.0").IsSuccess);
        Assert.False(ReleaseManifest.Parse("{\"schemaVersion\":1,\"unknown\":true}", "0.6.0").IsSuccess);
    }

    [Fact]
    public void Committed_release_source_matches_program_and_frozen_index()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mosaic.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var version = typeof(ReleaseSummaryModule).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var source = File.ReadAllText(Path.Combine(directory.FullName, "build/release-content.json"));
        var manifest = ReleaseManifest.Parse(source, version);
        Assert.True(manifest.IsSuccess, manifest.Error?.Message);
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "build/published-release-index.json")));
        foreach (var entry in index.RootElement.EnumerateArray())
        {
            var release = Assert.Single(manifest.Value.Releases, item => item.ReleaseId == entry.GetProperty("releaseId").GetString());
            Assert.Equal(entry.GetProperty("version").GetString(), release.Version);
            Assert.Equal(entry.GetProperty("contentSha256").GetString(), release.ContentSha256);
        }
    }

    [Fact]
    public void Shipped_manifest_announces_every_version_after_the_last_receipt()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mosaic.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var version = typeof(ReleaseSummaryModule).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var manifest = ReleaseManifest.Parse(File.ReadAllText(Path.Combine(directory.FullName, "build/release-content.json")), version);
        Assert.True(manifest.IsSuccess, manifest.Error?.Message);
        var result = ReleaseSummaryComposer.Compose(manifest.Value, ["mosaic-0.12.2"]);
        Assert.True(result.IsSuccess, result.Error?.Message);
        var latest = manifest.Value.Releases[^1];
        Assert.Contains("[0.13.0]", result.Value.Notes);
        Assert.Contains($"[{latest.Version}]", result.Value.Notes);
        Assert.Equal(latest.ReleaseId, result.Value.ReleaseId);
        Assert.Empty(ReleaseSummaryComposer.Compose(manifest.Value, [latest.ReleaseId]).Value.Notes);
    }

    [Fact]
    public async Task Published_start_creates_once_with_explicit_incomplete_state_and_cached_attempt()
    {
        var tasks = new Tasks();
        var settings = new Settings();
        var diagnostics = new Diagnostics();
        await using var module = Module(tasks, settings, diagnostics);
        await module.ActivateAsync();
        Assert.Equal(0, tasks.CreateCalls);
        var first = await module.Api.RequestStartupReadyAsync();
        var second = await module.Api.RequestStartupReadyAsync();
        Assert.True(first.IsSuccess);
        Assert.Same(first, second);
        Assert.Equal(1, tasks.CreateCalls);
        Assert.Equal(1, tasks.ReadCalls);
        Assert.Equal("更新简要", tasks.LastRequest!.Title);
        Assert.False(tasks.LastRequest.IsCompleted);
        Assert.Empty(diagnostics.Failures);
        var definition = Assert.Single(settings.Source!.Definitions);
        Assert.Equal(SettingsGroup.Tasks, definition.Group);
        Assert.Equal(SettingCategory.Normal, definition.Category);
        Assert.Equal(SettingApplyMode.Restart, definition.ApplyMode);
        Assert.Equal(SettingPreviewMode.None, definition.PreviewMode);
        Assert.Equal(SettingPersistence.UserSettings, definition.Persistence);
    }

    [Theory]
    [InlineData(false, true, ReleaseSummaryDisposition.DevelopmentBuild)]
    [InlineData(true, false, ReleaseSummaryDisposition.Disabled)]
    public async Task Disabled_and_development_start_have_zero_task_requests(bool published, bool enabled, ReleaseSummaryDisposition expected)
    {
        var tasks = new Tasks();
        await using var module = Module(tasks, new Settings { Enabled = enabled }, new Diagnostics(), published);
        await module.ActivateAsync();
        var result = await module.Api.RequestStartupReadyAsync();
        Assert.Equal(expected, result.Value.Disposition);
        Assert.Equal(0, tasks.ReadCalls);
        Assert.Equal(0, tasks.CreateCalls);
    }

    [Fact]
    public async Task Receipt_read_failure_never_creates_and_can_retry_on_next_process()
    {
        var tasks = new Tasks { ReadFailure = new(ErrorCodes.StorageUnavailable, "disk") };
        var diagnostics = new Diagnostics();
        await using (var module = Module(tasks, new Settings(), diagnostics))
        {
            await module.ActivateAsync();
            Assert.False((await module.Api.RequestStartupReadyAsync()).IsSuccess);
            Assert.False((await module.Api.RequestStartupReadyAsync()).IsSuccess);
            Assert.Equal(1, tasks.ReadCalls);
            Assert.Equal(0, tasks.CreateCalls);
        }
        tasks.ReadFailure = null;
        await using var restarted = Module(tasks, new Settings(), diagnostics);
        await restarted.ActivateAsync();
        Assert.True((await restarted.Api.RequestStartupReadyAsync()).IsSuccess);
        Assert.Equal(1, tasks.CreateCalls);
        Assert.NotEmpty(diagnostics.Failures);
    }

    [Fact]
    public async Task Deleted_or_edited_task_receipt_prevents_recreation_and_downgrade()
    {
        var tasks = new Tasks { Receipts = [new("release-summary", "mosaic-0.6.0", Guid.NewGuid(), "original", DateTimeOffset.UtcNow, false)] };
        await using var module = Module(tasks, new Settings(), new Diagnostics());
        await module.ActivateAsync();
        Assert.Equal(ReleaseSummaryDisposition.AlreadyAnnounced, (await module.Api.RequestStartupReadyAsync()).Value.Disposition);
        Assert.Equal(0, tasks.CreateCalls);
    }

    [Fact]
    public async Task Unknown_future_receipt_in_a_downgraded_package_never_creates()
    {
        var tasks = new Tasks { Receipts = [new("release-summary", "mosaic-9.0.0", Guid.NewGuid(), "future", DateTimeOffset.UtcNow, true)] };
        var diagnostics = new Diagnostics();
        await using var module = Module(tasks, new Settings(), diagnostics);
        await module.ActivateAsync();
        Assert.False((await module.Api.RequestStartupReadyAsync()).IsSuccess);
        Assert.Equal(0, tasks.CreateCalls);
        Assert.Single(diagnostics.Failures);
    }

    [Fact]
    public async Task Create_failure_is_diagnosed_without_success_receipt_or_startup_exception()
    {
        var tasks = new Tasks { CreateFailure = new(ErrorCodes.ValidationFailed, "notes limit", "notes") };
        var diagnostics = new Diagnostics();
        await using var module = Module(tasks, new Settings(), diagnostics);
        await module.ActivateAsync();
        var result = await module.Api.RequestStartupReadyAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.Empty(tasks.Receipts);
        Assert.Single(diagnostics.Failures);
    }

    private static ReleaseSummaryModule Module(Tasks tasks, Settings settings, Diagnostics diagnostics, bool published = true) =>
        new(new(tasks, settings, diagnostics, new(Json(Manifest(Release("0.5.1"), Release("0.6.0", "0.5.1"))), "0.6.0", published)));

    private sealed class Tasks : ITaskCreateOnceApi
    {
        internal int ReadCalls;
        internal int CreateCalls;
        internal OperationError? ReadFailure;
        internal OperationError? CreateFailure;
        internal CreateTaskOnceRequest? LastRequest;
        internal IReadOnlyList<TaskCreationReceiptDto> Receipts = [];
        public Task<OperationResult<IReadOnlyList<TaskCreationReceiptDto>>> ListReceiptsAsync(string sourceId, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return Task.FromResult(ReadFailure is null
                ? OperationResult<IReadOnlyList<TaskCreationReceiptDto>>.Success(Receipts)
                : OperationResult<IReadOnlyList<TaskCreationReceiptDto>>.Failure(ReadFailure));
        }
        public Task<OperationResult<TaskCreateOnceResult>> CreateOnceAsync(CreateTaskOnceRequest request, CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            LastRequest = request;
            return Task.FromResult(CreateFailure is null
                ? OperationResult<TaskCreateOnceResult>.Success(new(Guid.NewGuid(), TaskCreateOnceDisposition.Created, true))
                : OperationResult<TaskCreateOnceResult>.Failure(CreateFailure));
        }
    }

    private sealed class Diagnostics : IDiagnosticSink
    {
        internal List<Exception> Failures { get; } = [];
        public void Report(string correlationId, Exception exception) => Failures.Add(exception);
    }

    private sealed class Settings : ISettingsCore
    {
        internal bool Enabled = true;
        internal ISettingSource? Source;
        public IDisposable Register(ISettingSource source) { Source = source; return new Registration(); }
        public Task<OperationResult<IReadOnlyDictionary<string, object?>>> GetValuesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, object?>>.Success(
                new Dictionary<string, object?> { ["updates.summaryTask.enabled"] = Enabled }));
        public Task<OperationResult<IReadOnlyList<SettingDefinition>>> ListDefinitionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> ResetAsync(IReadOnlyList<string> definitionIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        private sealed class Registration : IDisposable { public void Dispose() { } }
    }
}
