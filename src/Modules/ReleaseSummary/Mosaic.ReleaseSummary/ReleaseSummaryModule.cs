using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.ReleaseSummary;

public sealed record ReleaseSummaryStartupInput(string ManifestJson, string ProgramVersion, bool IsPublishedBuild);
public sealed record ReleaseSummaryDependencies(ITaskCreateOnceApi Tasks, ISettingsCore Settings,
    IDiagnosticSink Diagnostics, ReleaseSummaryStartupInput Input);
public enum ReleaseSummaryDisposition { Created, AlreadyCreated, AlreadyAnnounced, Disabled, DevelopmentBuild }
public sealed record ReleaseSummaryAttempt(ReleaseSummaryDisposition Disposition, Guid? TaskId = null);
public interface IReleaseSummaryApi
{
    Task<OperationResult<ReleaseSummaryAttempt>> RequestStartupReadyAsync(CancellationToken cancellationToken = default);
}

public sealed class ReleaseSummaryModule : IModuleActivation, IReleaseSummaryApi
{
    private readonly ReleaseSummaryDependencies _dependencies;
    private readonly object _sync = new();
    private IDisposable? _registration;
    private Task<OperationResult<ReleaseSummaryAttempt>>? _attempt;
    private bool _active;
    private bool _enabled;

    public ReleaseSummaryModule(ReleaseSummaryDependencies dependencies) => _dependencies = dependencies;
    public static ModuleManifest Definition { get; } = new("release-summary", [new("release.summary", 1)],
        [new("tasks.create-once", 1), new("settings.api", 1)],
        "Owns release content, announced version ranges and update-summary task policy.");
    public ModuleManifest Manifest => Definition;
    public IReleaseSummaryApi Api => this;

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        _registration ??= _dependencies.Settings.Register(new ReleaseSummarySettings());
        var values = await _dependencies.Settings.GetValuesAsync(cancellationToken).ConfigureAwait(false);
        if (!values.IsSuccess) throw new InvalidOperationException("更新任务设置无法读取：" + values.Error!.Message);
        _enabled = !values.Value.TryGetValue(ReleaseSummarySettings.EnabledId, out var value) || value is true;
        lock (_sync) _active = true;
    }

    public Task<OperationResult<ReleaseSummaryAttempt>> RequestStartupReadyAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_active)
                return Task.FromResult(OperationResult<ReleaseSummaryAttempt>.Failure(
                    new(ErrorCodes.CapabilityUnavailable, "更新简要模块尚未启用或正在关闭。")));
            return _attempt ??= AttemptAsync(cancellationToken);
        }
    }

    private async Task<OperationResult<ReleaseSummaryAttempt>> AttemptAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_dependencies.Input.IsPublishedBuild) return Success(ReleaseSummaryDisposition.DevelopmentBuild);
            if (!_enabled) return Success(ReleaseSummaryDisposition.Disabled);
            var manifest = ReleaseManifest.Parse(_dependencies.Input.ManifestJson, _dependencies.Input.ProgramVersion);
            if (!manifest.IsSuccess) return Failure(manifest.Error!);
            var receipts = await _dependencies.Tasks.ListReceiptsAsync(ReleaseSummaryComposer.SourceId, cancellationToken).ConfigureAwait(false);
            if (!receipts.IsSuccess) return Failure(receipts.Error!);
            var document = ReleaseSummaryComposer.Compose(manifest.Value, receipts.Value.Select(item => item.RequestKey).ToArray());
            if (!document.IsSuccess) return Failure(document.Error!);
            if (document.Value.Notes.Length == 0) return Success(ReleaseSummaryDisposition.AlreadyAnnounced);
            var created = await _dependencies.Tasks.CreateOnceAsync(new(
                ReleaseSummaryComposer.SourceId, document.Value.ReleaseId, ReleaseSummaryComposer.TaskTitle,
                document.Value.Notes, false), cancellationToken).ConfigureAwait(false);
            if (!created.IsSuccess) return Failure(created.Error!);
            return OperationResult<ReleaseSummaryAttempt>.Success(new(
                created.Value.Disposition == TaskCreateOnceDisposition.Created
                    ? ReleaseSummaryDisposition.Created : ReleaseSummaryDisposition.AlreadyCreated,
                created.Value.TaskId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(new(ErrorCodes.Canceled, "更新简要生成已取消，下次启动重试。"));
        }
        catch (Exception exception)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            Report(correlationId, exception);
            return OperationResult<ReleaseSummaryAttempt>.Failure(new(ErrorCodes.InternalError,
                "更新简要暂未生成，下次启动重试。", CorrelationId: correlationId));
        }
    }

    private OperationResult<ReleaseSummaryAttempt> Failure(OperationError error)
    {
        var correlationId = error.CorrelationId ?? Guid.NewGuid().ToString("N");
        Report(correlationId,
            new InvalidOperationException($"更新简要待生成 [{error.Code}]：{error.Message}"));
        return OperationResult<ReleaseSummaryAttempt>.Failure(error with { CorrelationId = correlationId });
    }

    private static OperationResult<ReleaseSummaryAttempt> Success(ReleaseSummaryDisposition disposition) =>
        OperationResult<ReleaseSummaryAttempt>.Success(new(disposition));

    private void Report(string correlationId, Exception exception)
    {
        try { _dependencies.Diagnostics.Report(correlationId, exception); }
        catch { /* Diagnostics must never turn an optional announcement failure into a startup failure. */ }
    }

    public async Task StopAsync()
    {
        Task<OperationResult<ReleaseSummaryAttempt>>? attempt;
        lock (_sync) { _active = false; attempt = _attempt; }
        if (attempt is not null) await attempt.ConfigureAwait(false);
        if (_registration is not null)
        {
            var result = await _dependencies.Settings.PrepareSourceStopAsync("release-summary").ConfigureAwait(false);
            if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
            _registration.Dispose();
            _registration = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

internal sealed class ReleaseSummarySettings : ISettingSource
{
    internal const string EnabledId = "updates.summaryTask.enabled";
    public string ModuleId => "release-summary";
    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(EnabledId, "release-summary", "自动生成更新简要", "下次启动为新发布版本生成一条更新简要任务",
            SettingValueKind.Boolean, true, null, null, null, SettingsGroup.Tasks, SettingCategory.Normal,
            SettingApplyMode.Restart, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.None,
            DisplayNameKey: "Settings.Setting.UpdateSummaryEnabled", DescriptionKey: "Settings.Setting.UpdateSummaryEnabled.Description")
    ];
    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
