using System.Globalization;
using Mosaic.Mobile.Core.Api;
using Mosaic.Mobile.Core.Auth;
using Mosaic.Mobile.Core.Cards;
using Mosaic.Mobile.Core.Commands;
using Mosaic.Mobile.Core.Configuration;
using Mosaic.Mobile.Core.Projection;
using Mosaic.Mobile.Core.Sync;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Core;

public sealed record CardActionResult(CommandOutcome Outcome, string Message, CardModel Model)
{
    public bool IsSuccess => Outcome is CommandOutcome.Applied or CommandOutcome.Replayed or CommandOutcome.AlreadyApplied;
}

/// <summary>
/// The no-UI kernel the Android widget drives. Every entry point is safe to
/// call from a broadcast receiver: configuration is read from a replaceable
/// provider, the session is restored without any Activity, the projection is
/// opened only for a verified identity, and the only write is the single-task
/// command.
/// </summary>
public sealed class MobileKernel
{
    private readonly IServerConfigProvider _config;
    private readonly SessionManager _session;
    private readonly IMosaicApiFactory _apiFactory;
    private readonly SnapshotBootstrapService _bootstrap;
    private readonly IncrementalSyncService _pull;
    private readonly TaskCommandService _commands;
    private readonly IFluidCloudAdapter _fluidCloud;
    private readonly string _appDataDirectory;

    public MobileKernel(
        IServerConfigProvider config,
        SessionManager session,
        IMosaicApiFactory apiFactory,
        string appDataDirectory,
        TimeProvider? time = null,
        IFluidCloudAdapter? fluidCloud = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(apiFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);

        _config = config;
        _session = session;
        _apiFactory = apiFactory;
        _appDataDirectory = appDataDirectory;
        _fluidCloud = fluidCloud ?? new UnavailableFluidCloudAdapter();
        var clock = time ?? TimeProvider.System;
        _bootstrap = new SnapshotBootstrapService(clock);
        _pull = new IncrementalSyncService();
        _commands = new TaskCommandService(session, clock);
    }

    public async Task<CardModel> RefreshAsync(CancellationToken cancellationToken)
    {
        Resolution resolution;
        try
        {
            resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (resolution.Failure is not null)
        {
            await PublishAsync(resolution.Failure, cancellationToken).ConfigureAwait(false);
            return resolution.Failure;
        }

        try
        {
            var projection = resolution.Projection!;
            var model = await BuildModelAsync(projection, resolution.Offline, cancellationToken).ConfigureAwait(false);
            await PublishAsync(model, cancellationToken).ConfigureAwait(false);
            return model;
        }
        finally
        {
            await resolution.DisposeProjectionAsync().ConfigureAwait(false);
        }
    }

    public async Task<CardActionResult> ExecuteAsync(Guid taskId, CardAction action, CancellationToken cancellationToken)
    {
        if (action == CardAction.Refresh)
        {
            var refreshed = await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return new CardActionResult(CommandOutcome.Applied, "已刷新", refreshed);
        }

        if (!CardActionCodec.TryParseTaskAction(action, out var taskAction))
        {
            return new CardActionResult(
                CommandOutcome.Protocol,
                "不支持的操作",
                CardModelBuilder.Status(CardState.Unavailable, _fluidCloud.Status, "该操作不在允许范围内"));
        }

        Resolution resolution;
        try
        {
            resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (resolution.Failure is not null)
        {
            var outcome = resolution.Failure.State switch
            {
                CardState.SessionExpired or CardState.NeedsSignIn => CommandOutcome.SessionExpired,
                CardState.Offline => CommandOutcome.Offline,
                _ => CommandOutcome.NotReady,
            };
            return new CardActionResult(outcome, resolution.Failure.Detail, resolution.Failure);
        }

        try
        {
            var projection = resolution.Projection!;
            var result = await _commands
                .ExecuteAsync(
                    resolution.Api!,
                    projection,
                    taskId,
                    taskAction,
                    resolution.DeviceId!,
                    cancellationToken)
                .ConfigureAwait(false);

            var model = await BuildModelAsync(projection, resolution.Offline, cancellationToken).ConfigureAwait(false);
            await PublishAsync(model, cancellationToken).ConfigureAwait(false);
            return new CardActionResult(result.Outcome, result.Message, model);
        }
        finally
        {
            await resolution.DisposeProjectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<Resolution> ResolveAsync(CancellationToken cancellationToken)
    {
        var configuration = await _config.GetAsync(cancellationToken).ConfigureAwait(false);
        if (configuration is null)
        {
            return Resolution.Failed(CardModelBuilder.Status(CardState.NeedsConfiguration, _fluidCloud.Status));
        }

        var restore = await _session.RestoreAsync(configuration, cancellationToken).ConfigureAwait(false);
        switch (restore.State)
        {
            case SessionState.SignedOut:
                return Resolution.Failed(CardModelBuilder.Status(CardState.NeedsSignIn, _fluidCloud.Status));
            case SessionState.Expired:
                return Resolution.Failed(CardModelBuilder.Status(CardState.SessionExpired, _fluidCloud.Status));
            case SessionState.Unavailable:
                return Resolution.Failed(CardModelBuilder.Status(CardState.Unavailable, _fluidCloud.Status, "安全存储不可用，无法恢复会话"));
        }

        var identity = ProjectionIdentity.Create(configuration.NormalizedOrigin, restore.UserId!);
        var open = await TaskProjection.OpenAsync(identity, _appDataDirectory, cancellationToken).ConfigureAwait(false);
        if (!open.IsOpened)
        {
            return open.Status switch
            {
                ProjectionOpenStatus.OwnerMismatch => Resolution.Failed(
                    CardModelBuilder.Status(CardState.Unavailable, _fluidCloud.Status, "本地投影属于其他账号，已隔离")),
                _ => Resolution.Failed(
                    CardModelBuilder.Status(CardState.NeedsRestore, _fluidCloud.Status, "本地投影不可用，等待重建")),
            };
        }

        var projection = open.Projection!;
        try
        {
            var api = _apiFactory.Create(configuration.BaseUri);
            var accessToken = await _session.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(accessToken))
            {
                await projection.DisposeAsync().ConfigureAwait(false);
                return Resolution.Failed(CardModelBuilder.Status(CardState.SessionExpired, _fluidCloud.Status));
            }

            var deviceId = await _session.GetOrCreateDeviceIdAsync(cancellationToken).ConfigureAwait(false);
            var offline = restore.Offline;

            if (projection.Baseline is null)
            {
                var bootstrap = await _bootstrap.RunAsync(api, projection, accessToken, cancellationToken).ConfigureAwait(false);
                var mapped = await MapBootstrapFailureAsync(bootstrap, cancellationToken).ConfigureAwait(false);
                if (mapped is not null)
                {
                    await projection.DisposeAsync().ConfigureAwait(false);
                    return mapped;
                }
            }

            var pull = await _pull.RunAsync(api, projection, accessToken, cancellationToken).ConfigureAwait(false);
            switch (pull.Outcome)
            {
                case PullOutcome.SessionExpired:
                    await _session.InvalidateAsync(cancellationToken).ConfigureAwait(false);
                    await projection.DisposeAsync().ConfigureAwait(false);
                    return Resolution.Failed(CardModelBuilder.Status(CardState.SessionExpired, _fluidCloud.Status));
                case PullOutcome.Cancelled:
                    throw new OperationCanceledException(cancellationToken);
                case PullOutcome.NeedsSnapshot:
                    var rebootstrap = await _bootstrap.RunAsync(api, projection, accessToken, cancellationToken).ConfigureAwait(false);
                    var rebootstrapFailure = await MapBootstrapFailureAsync(rebootstrap, cancellationToken).ConfigureAwait(false);
                    if (rebootstrapFailure is not null)
                    {
                        await projection.DisposeAsync().ConfigureAwait(false);
                        return rebootstrapFailure;
                    }

                    var second = await _pull.RunAsync(api, projection, accessToken, cancellationToken).ConfigureAwait(false);
                    offline |= second.Outcome is PullOutcome.Offline or PullOutcome.Error;
                    break;
                case PullOutcome.Protocol:
                    await projection.DisposeAsync().ConfigureAwait(false);
                    return Resolution.Failed(
                        CardModelBuilder.Status(CardState.Unavailable, _fluidCloud.Status, "服务端任务数据无法应用，请更新客户端"));
                case PullOutcome.Offline:
                case PullOutcome.Error:
                    offline = true;
                    break;
            }

            return new Resolution(null, projection, api, accessToken, deviceId, offline);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await projection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or SecretStorageException)
        {
            await projection.DisposeAsync().ConfigureAwait(false);
            return Resolution.Failed(CardModelBuilder.Status(CardState.Unavailable, _fluidCloud.Status, "本地投影无法读取"));
        }
        catch
        {
            await projection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Resolution?> MapBootstrapFailureAsync(BootstrapResult bootstrap, CancellationToken cancellationToken)
    {
        switch (bootstrap.Outcome)
        {
            case BootstrapOutcome.Restored:
                return null;
            case BootstrapOutcome.NoSnapshot:
                return Resolution.Failed(CardModelBuilder.Status(CardState.NeedsRestore, _fluidCloud.Status, "服务端还没有可用备份，请先在桌面端上传"));
            case BootstrapOutcome.SessionExpired:
                await _session.InvalidateAsync(cancellationToken).ConfigureAwait(false);
                return Resolution.Failed(CardModelBuilder.Status(CardState.SessionExpired, _fluidCloud.Status));
            case BootstrapOutcome.Offline:
                return Resolution.Failed(CardModelBuilder.Status(CardState.Offline, _fluidCloud.Status, "离线，尚未建立本地投影"));
            case BootstrapOutcome.TooLarge:
                return Resolution.Failed(CardModelBuilder.Status(CardState.NeedsRestore, _fluidCloud.Status, "备份超过手机端处理上限"));
            case BootstrapOutcome.Corrupt:
                return Resolution.Failed(CardModelBuilder.Status(CardState.Unavailable, _fluidCloud.Status, "服务端备份校验失败"));
            case BootstrapOutcome.Cancelled:
                throw new OperationCanceledException(cancellationToken);
            default:
                return Resolution.Failed(CardModelBuilder.Status(CardState.Unavailable, _fluidCloud.Status, "服务端备份读取失败"));
        }
    }

    private async Task<CardModel> BuildModelAsync(TaskProjection projection, bool offline, CancellationToken cancellationToken)
    {
        var counts = await projection.CountAsync(cancellationToken).ConfigureAwait(false);
        var tasks = await projection.ListVisibleAsync(CardModelBuilder.RowLimit, cancellationToken).ConfigureAwait(false);
        var rows = tasks
            .Select(task => new CardTaskRow(task.Id, ShortenTitle(task.Title), task.IsCompleted))
            .ToList();

        var fluid = _fluidCloud.Status;
        var model = counts.Visible == 0
            ? CardModelBuilder.Empty(fluid)
            : CardModelBuilder.Ready(counts, rows, fluid);

        return offline
            ? model with { State = CardState.Offline, Detail = "离线 · " + model.Detail }
            : model;
    }

    private async Task PublishAsync(CardModel model, CancellationToken cancellationToken)
    {
        try
        {
            await _fluidCloud.PublishAsync(model, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A vendor surface failure never breaks the widget path.
        }
    }

    private static string ShortenTitle(string title)
    {
        const int limit = 80;
        if (title.Length <= limit)
        {
            return title;
        }

        return string.Concat(title.AsSpan(0, limit - 1), "…");
    }

    private sealed record Resolution(
        CardModel? Failure,
        TaskProjection? Projection,
        IMosaicApi? Api,
        string? AccessToken,
        string? DeviceId,
        bool Offline)
    {
        public static Resolution Failed(CardModel model) => new(model, null, null, null, null, false);

        public async Task DisposeProjectionAsync()
        {
            if (Projection is not null)
            {
                await Projection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}