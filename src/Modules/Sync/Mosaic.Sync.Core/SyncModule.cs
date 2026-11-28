using Mosaic.Auth.Contracts;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Sync.Contracts;
using Mosaic.Sync.Core.Internal;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Sync.Core;

public sealed record SyncDependencies(
    IAuthApi Auth,
    ISyncTransport Transport,
    ITaskTransferApi Tasks,
    ISettingsCore Settings,
    IModuleExecutionGate Gate,
    IDiagnosticSink Diagnostics,
    TimeProvider? Clock = null,
    ITaskChangeSource? TaskChanges = null);

public sealed class SyncModule : IModuleActivation
{
    private readonly SyncService _service;
    private readonly GuardedSyncApi _api;
    private readonly IAuthApi _auth;
    private readonly ISettingsCore _settings;
    private readonly IModuleExecutionGate _gate;
    private readonly IDiagnosticSink _diagnostics;
    private readonly TimeProvider _clock;
    private readonly ITaskChangeSource? _taskChanges;
    private readonly SyncOptions _options = new();
    // 等待期间的唤醒信号：登录、设置改间隔、停止都通过它打断 Task.Delay，避免轮询。
    private TaskCompletionSource<bool> _wakeSignal = NewSignal();
    private int _uploadDue;
    private IDisposable? _settingsRegistration;
    private IDisposable? _taskChangeSubscription;
    private EventHandler? _authChanged;
    private CancellationTokenSource? _autoSync;
    private Task? _autoSyncLoop;

    public SyncModule(SyncDependencies dependencies)
    {
        _auth = dependencies.Auth;
        _settings = dependencies.Settings;
        _gate = dependencies.Gate;
        _diagnostics = dependencies.Diagnostics;
        _clock = dependencies.Clock ?? TimeProvider.System;
        _taskChanges = dependencies.TaskChanges;
        _service = new(dependencies.Auth, dependencies.Transport, dependencies.Tasks, dependencies.Settings, _options);
        _api = new GuardedSyncApi(_service, dependencies.Gate, dependencies.Diagnostics);
    }

    public static ModuleManifest Definition { get; } = new(
        "sync-core",
        [new("sync.api", 1)],
        [new("auth.api", 1), new("sync.transport", 1), new("tasks.transfer", 1), new("settings.api", 1)],
        "Uploads and restores task and portable setting backups through the account server.");

    public ModuleManifest Manifest => Definition;

    public ISyncApi Api => _api;

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _settingsRegistration ??= _settings.Register(new SyncSettingsSource(_options, RetimeAutoSync));
        // 激活时先读出已落盘的设置：自动备份的开关与间隔必须在后台循环启动前就是用户保存的值，
        // 否则首次运行会把“已关闭的备份”当成默认开启。
        var values = await _settings.GetValuesAsync(cancellationToken).ConfigureAwait(false);
        if (!values.IsSuccess) throw new InvalidOperationException("同步设置读取失败。", new Exception(values.Error?.Message));
        _options.Apply(values.Value);

        _authChanged ??= (_, _) => OnAuthChanged();
        _auth.StateChanged += _authChanged;
        // 一次保存只发布一条任务变更；它必须立即排队上传，不能等下一个 60 秒周期。
        _taskChangeSubscription ??= _taskChanges?.Subscribe(OnTaskChanged);
        _gate.Activate();

        _autoSync = new CancellationTokenSource();
        _autoSyncLoop = AutoSyncLoopAsync(_autoSync.Token);
    }

    public async Task StopAsync()
    {
        _taskChangeSubscription?.Dispose();
        _taskChangeSubscription = null;
        if (_authChanged is not null) _auth.StateChanged -= _authChanged;
        // 后台循环必须在注销设置源与关闭执行门之前取消并等待，循环内部不再持有设置与网关租约。
        if (_autoSync is { } autoSync)
        {
            await autoSync.CancelAsync().ConfigureAwait(false);
            if (_autoSyncLoop is { } loop)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            autoSync.Dispose();
            _autoSync = null;
            _autoSyncLoop = null;
        }
        var result = await _settings.PrepareSourceStopAsync("sync-core").ConfigureAwait(false);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
        _settingsRegistration?.Dispose();
        _settingsRegistration = null;
        await _gate.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void OnAuthChanged()
    {
        _service.RaiseForAuthChange();
        // 登录成功后立即备份一次，之后按设置里的间隔继续；登出由状态变化驱动，循环下一轮会自行跳过。
        if (_auth.State.Status == AuthStatus.SignedIn) ScheduleUpload();
    }

    private void OnTaskChanged(TaskChange _)
    {
        // 恢复整表也会走这里：上传只读快照，不会再次改动本地任务或触发第二轮回写。
        if (_auth.State.Status == AuthStatus.SignedIn) ScheduleUpload();
    }

    // 设置里的间隔或开关变化只打断当前等待，让循环按新间隔重新计时，不会顺带触发一次上传。
    private void RetimeAutoSync() => Wake();

    private void ScheduleUpload()
    {
        Interlocked.Exchange(ref _uploadDue, 1);
        Wake();
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Wake()
    {
        // 换掉当前信号再完成它：已捕获旧信号的等待立刻返回，之后的等待不会被同一事件穿透。
        var signal = Interlocked.Exchange(ref _wakeSignal, NewSignal());
        signal.TrySetResult(true);
    }

    /// <summary>等待一个自动备份间隔；返回 <c>true</c> 表示间隔到点，<c>false</c> 表示被唤醒或已停止。</summary>
    private async Task<bool> WaitForTickAsync(Task signal, CancellationToken cancellationToken)
    {
        var delay = Task.Delay(_options.AutoInterval, _clock, cancellationToken);
        if (ReferenceEquals(await Task.WhenAny(delay, signal).ConfigureAwait(false), delay))
        {
            try { await delay.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return true;
        }
        // 被唤醒时放弃本次计时：停止时它会以取消结束，提前观察以避免未观测的任务异常。
        _ = delay.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return false;
    }

    private async Task AutoSyncLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var signal = Volatile.Read(ref _wakeSignal).Task;
            var elapsed = await WaitForTickAsync(signal, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return;
            var due = Interlocked.Exchange(ref _uploadDue, 0);
            // 间隔到点无条件备份；仅被唤醒时只有“登录后立即备份”需要动作，重定时只重新排队。
            if (!elapsed && due == 0) continue;
            try { await AutoSyncOnceAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                // 周期任务不能因为一次失败终止：记一条诊断后等下一轮重试。
                try { _diagnostics.Report(Guid.NewGuid().ToString("N"), exception); } catch { }
            }
        }
    }

    private async Task AutoSyncOnceAsync(CancellationToken cancellationToken)
    {
        // 未登录、备份开关关闭或自动备份关闭时静默跳过；这些都是用户可见的设置，不需要每轮报错。
        if (!_options.Enabled || !_options.AutoSyncEnabled) return;
        if (_auth.State is not { Status: AuthStatus.SignedIn, Server: not null }) return;
        // 只上传任务与用户设置，不下载、不镜像附件：用户正在编辑的数据不能被后台备份覆盖。
        await _api.UploadTasksAsync(cancellationToken).ConfigureAwait(false);
    }
}