using Mosaic.Auth.Contracts;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Sync.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Sync.Core.Tests;

/// <summary>自动备份后台循环的行为：登录触发一次、按间隔重复、登出/关闭时静默跳过、改间隔即时重定时、停止即收手。</summary>
public sealed class AutomaticSyncTests
{
    private static readonly Guid TaskId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Uri Server = new("https://sync.example.com/");

    private sealed class Fixture
    {
        public FakeAuthApi Auth { get; } = new();
        public FakeSyncTransport Transport { get; } = new();
        public FakeTaskTransferApi Tasks { get; } = new();
        public FakeTaskChangeSource Changes { get; } = new();
        public FakeSettings Settings { get; } = new();
        public Diagnostics Diagnostics { get; } = new();
        public ManualTimeProvider Clock { get; } = new();
        public SyncModule? Module { get; private set; }

        public async Task<Fixture> StartAsync(int? intervalSeconds = null, bool? autoSyncEnabled = null, bool addTask = true)
        {
            // 落盘值必须在激活前写入：激活时读取设置，后台循环按用户保存的间隔开始计时。
            if (intervalSeconds is { } seconds) Settings.Values["sync.autoIntervalSeconds"] = seconds;
            if (autoSyncEnabled is { } enabled) Settings.Values["sync.autoSyncEnabled"] = enabled;
            if (addTask) AddTask();
            Module = new SyncModule(new(Auth, Transport, Tasks, Settings, new Gate(), Diagnostics, Clock, Changes));
            await Module.ActivateAsync();
            await Settings.ApplySourcesAsync();
            return this;
        }

        public void AddTask(string title = "任务") =>
            Tasks.Tasks.Add(new(TaskId, title, "", false,
                new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), 7));

        public void RaiseSignedIn()
        {
            Auth.State = new(AuthStatus.SignedIn, new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "user@example.com", "用户"), Server);
            Auth.Raise();
        }

        public void RaiseSignedOut()
        {
            Auth.State = AuthState.SignedOut(Server);
            Auth.Raise();
        }

        /// <summary>模拟设置编辑提交：写入新值后让已登记的设置源应用它（真实环境里这会触发 <c>ApplyAsync</c>）。</summary>
        public async Task CommitAsync(string id, object? value)
        {
            Settings.Values[id] = value;
            await Settings.ApplySourcesAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("等待超时：" + because);
            await Task.Delay(10);
        }
    }

    /// <summary>按秒推进手动时钟，直到条件成立；循环每次只推进一秒，避免跨过两个间隔。</summary>
    private static async Task AdvanceUntilAsync(ManualTimeProvider clock, Func<bool> condition, string because, int maxSeconds = 180)
    {
        for (var second = 0; second < maxSeconds && !condition(); second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
        Assert.True(condition(), because);
    }

    /// <summary>逐秒推进指定秒数，用于断言“这段时间内什么都不该发生”。</summary>
    private static async Task AdvanceForAsync(ManualTimeProvider clock, int seconds)
    {
        for (var second = 0; second < seconds; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
        // 留出后台循环完成（或放弃）本轮的时间，避免在续体尚未运行时误判。
        await Task.Delay(50);
    }

    private static void AssertUploadedOnce(Fixture fixture)
    {
        Assert.Equal(1, fixture.Transport.Uploads);
        Assert.Empty(fixture.Diagnostics.Reported);
        // 自动备份只上传任务与可移植设置：不下载快照、不列附件清单、不传附件。
        Assert.Equal(0, fixture.Transport.DownloadRequests);
        Assert.Equal(0, fixture.Transport.ListAttachmentRequests);
        Assert.Empty(fixture.Transport.UploadedAttachments);
        Assert.All(fixture.Transport.LastUpload!, item => Assert.True(item.Kind is SyncItemKinds.Task or SyncItemKinds.Setting));
    }

    [Fact]
    public async Task Automatic_backup_does_not_replace_a_task_backup_with_settings_only()
    {
        var fixture = await new Fixture().StartAsync(addTask: false);
        fixture.Transport.LatestResult =
            OperationResult<BackupSnapshotDto?>.Success(FakeSyncTransport.Snapshot("snap-with-tasks", taskCount: 2));

        fixture.RaiseSignedIn();
        await Task.Delay(150);

        Assert.Equal(0, fixture.Transport.Uploads);
        Assert.Equal("snap-with-tasks", fixture.Module!.Api.State.LatestSnapshot!.SnapshotId);
        Assert.Empty(fixture.Diagnostics.Reported);
    }

    [Fact]
    public async Task Saving_a_task_uploads_before_the_interval_elapses()
    {
        var fixture = await new Fixture().StartAsync();
        await Task.Delay(50);
        Assert.Equal(0, fixture.Transport.Uploads);

        fixture.AddTask("刚保存的任务");
        fixture.Changes.Publish(new(TaskId, 8, TaskChangeKind.Created));

        await WaitUntilAsync(() => fixture.Transport.Uploads == 1, "任务保存后应立即上传，不能等下一个 60 秒周期");
        AssertUploadedOnce(fixture);
        var saved = fixture.Transport.LastUpload!.Single(item =>
        {
            if (item.Kind != SyncItemKinds.Task) return false;
            using var document = System.Text.Json.JsonDocument.Parse(item.Payload);
            return document.RootElement.GetProperty("title").GetString() == "刚保存的任务";
        });
        Assert.Equal(7, saved.Revision);
    }

    [Fact]
    public async Task Saving_a_task_during_a_failed_upload_is_retried_on_the_next_change()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.Transport.UploadResult = OperationResult<BackupSnapshotDto>.Failure(
            new(SyncErrorCodes.NetworkUnavailable, "连接中断"));
        fixture.RaiseSignedIn();
        await WaitUntilAsync(() => fixture.Transport.Uploads == 1, "首次上传失败也应完成一次请求");

        fixture.Transport.UploadResult = OperationResult<BackupSnapshotDto>.Success(
            FakeSyncTransport.Snapshot("snap-2"));
        fixture.AddTask("失败后保存的任务");
        fixture.Changes.Publish(new(TaskId, 8, TaskChangeKind.Created));

        await WaitUntilAsync(() => fixture.Transport.Uploads == 2, "失败后的任务变更应重新排队上传");
        Assert.Contains(fixture.Transport.LastUpload!, item =>
        {
            if (item.Kind != SyncItemKinds.Task) return false;
            using var document = System.Text.Json.JsonDocument.Parse(item.Payload);
            return document.RootElement.GetProperty("title").GetString() == "失败后保存的任务";
        });
    }

    [Fact]
    public async Task Task_changes_do_not_upload_while_signed_out()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.RaiseSignedOut();

        fixture.Changes.Publish(new(TaskId, 8, TaskChangeKind.Updated));
        await Task.Delay(150);

        Assert.Equal(0, fixture.Transport.Uploads);
        Assert.Empty(fixture.Diagnostics.Reported);
    }

    [Fact]
    public async Task Task_changes_respect_the_automatic_backup_switch()
    {
        var fixture = await new Fixture().StartAsync(autoSyncEnabled: false);
        await fixture.CommitAsync("sync.autoSyncEnabled", false);

        fixture.Changes.Publish(new(TaskId, 8, TaskChangeKind.Updated));
        await Task.Delay(150);

        Assert.Equal(0, fixture.Transport.Uploads);
        Assert.Empty(fixture.Diagnostics.Reported);
    }

    [Fact]
    public async Task Activating_starts_the_loop_without_uploading()
    {
        var fixture = await new Fixture().StartAsync();

        await Task.Delay(50);

        Assert.Equal(0, fixture.Transport.Uploads);
        Assert.Equal(0, fixture.Transport.DownloadRequests);
    }

    [Fact]
    public async Task Signing_in_uploads_once_without_downloading_or_mirroring_attachments()
    {
        var fixture = await new Fixture().StartAsync();

        fixture.RaiseSignedIn();

        await WaitUntilAsync(() => fixture.Transport.Uploads == 1, "登录后应立即备份一次");
        AssertUploadedOnce(fixture);
    }

    [Fact]
    public async Task Every_elapsed_interval_uploads_again()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.RaiseSignedIn();
        await WaitUntilAsync(() => fixture.Transport.Uploads == 1, "登录后应立即备份一次");

        await AdvanceUntilAsync(fixture.Clock, () => fixture.Transport.Uploads >= 2, "第一个 60 秒间隔到点应再备份一次");
        await AdvanceUntilAsync(fixture.Clock, () => fixture.Transport.Uploads >= 3, "第二个 60 秒间隔到点应再备份一次");

        Assert.Equal(3, fixture.Transport.Uploads);
        Assert.Empty(fixture.Diagnostics.Reported);
    }

    [Fact]
    public async Task Saved_interval_controls_when_the_first_upload_happens()
    {
        var fixture = await new Fixture().StartAsync(intervalSeconds: 15);

        fixture.Clock.Advance(TimeSpan.FromSeconds(14));
        await Task.Delay(50);
        Assert.Equal(0, fixture.Transport.Uploads);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => fixture.Transport.Uploads == 1, "15 秒到点应备份");
        AssertUploadedOnce(fixture);
    }

    [Fact]
    public async Task Signing_out_skips_ticks_and_signing_in_resumes()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.RaiseSignedOut();

        await AdvanceForAsync(fixture.Clock, 130);
        Assert.Equal(0, fixture.Transport.Uploads);

        fixture.RaiseSignedIn();
        await WaitUntilAsync(() => fixture.Transport.Uploads == 1, "重新登录后应立即备份一次");
        AssertUploadedOnce(fixture);
    }

    [Fact]
    public async Task Turning_automatic_backup_off_skips_ticks_until_it_is_enabled_again()
    {
        var fixture = await new Fixture().StartAsync();
        await fixture.CommitAsync("sync.autoSyncEnabled", false);

        await AdvanceForAsync(fixture.Clock, 130);
        Assert.Equal(0, fixture.Transport.Uploads);
        Assert.Empty(fixture.Diagnostics.Reported);

        await fixture.CommitAsync("sync.autoSyncEnabled", true);
        await AdvanceUntilAsync(fixture.Clock, () => fixture.Transport.Uploads == 1, "重新开启后下一个间隔到点应备份");
        AssertUploadedOnce(fixture);
    }

    [Fact]
    public async Task Shortening_the_interval_retimes_the_pending_wait()
    {
        var fixture = await new Fixture().StartAsync();
        // 先走过 30 秒；此时若仍在按 60 秒计时，剩余的 30 秒内不会备份。
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));

        await fixture.CommitAsync("sync.autoIntervalSeconds", 15);

        await AdvanceUntilAsync(fixture.Clock, () => fixture.Transport.Uploads == 1, "改小间隔后应按新间隔重新计时并备份", maxSeconds: 16);
        AssertUploadedOnce(fixture);
    }

    [Fact]
    public async Task Stopping_the_module_ends_the_background_loop()
    {
        var fixture = await new Fixture().StartAsync(intervalSeconds: 15);

        await fixture.Module!.StopAsync();

        Assert.Empty(fixture.Settings.Sources);
        fixture.Clock.Advance(TimeSpan.FromSeconds(600));
        await Task.Delay(50);
        Assert.Equal(0, fixture.Transport.Uploads);
        Assert.Empty(fixture.Diagnostics.Reported);
    }
}