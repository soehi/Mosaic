using System.Text.Json;
using Mosaic.Auth.Contracts;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Sync.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Sync.Core.Tests;

public sealed class SyncServiceTests
{
    private static readonly Guid TaskId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class Fixture
    {
        public FakeAuthApi Auth { get; } = new();
        public FakeSyncTransport Transport { get; } = new();
        public FakeTaskTransferApi Tasks { get; } = new();
        public FakeSettings Settings { get; } = new();
        public Diagnostics Diagnostics { get; } = new();
        public SyncModule? Module { get; private set; }
        public ISyncApi Api => Module!.Api;

        public async Task<Fixture> StartAsync(params (string Id, SettingPersistence Persistence)[] definitions)
        {
            foreach (var (id, persistence) in definitions)
                Settings.Definitions.Add(FakeSettings.Definition(id, persistence));
            Module = new SyncModule(new(Auth, Transport, Tasks, Settings, new Gate(), Diagnostics));
            await Module.ActivateAsync();
            await Settings.ApplySourcesAsync();
            return this;
        }

        public void AddTask(string title = "任务")
        {
            Tasks.Tasks.Add(new(TaskId, title, "", false,
                new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero), 7)
            {
                ArchivedAtUtc = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero)
            });
        }
    }

    private static string SettingPayload(object? value) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["value"] = value });

    [Fact]
    public async Task Upload_without_signing_in_is_refused_without_network_access()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.Auth.State = AuthState.SignedOut(new Uri("https://sync.example.com/"));

        var result = await fixture.Api.UploadAsync(true);

        Assert.False(result.IsSuccess);
        Assert.Equal(SyncErrorCodes.NotSignedIn, result.Error!.Code);
        Assert.Equal(0, fixture.Transport.Uploads);
        Assert.False(fixture.Api.State.IsSignedIn);
    }

    [Fact]
    public async Task Disabled_backup_refuses_upload_and_download()
    {
        var fixture = await new Fixture().StartAsync(("sync.enabled", SettingPersistence.UserSettings));
        fixture.Settings.Values["sync.enabled"] = false;
        await fixture.Settings.ApplySourcesAsync();

        var upload = await fixture.Api.UploadAsync(false);
        var download = await fixture.Api.DownloadAndReplaceAsync();

        Assert.Equal(SyncErrorCodes.Disabled, upload.Error!.Code);
        Assert.Equal(SyncErrorCodes.Disabled, download.Error!.Code);
        Assert.Equal(0, fixture.Transport.Uploads);
        Assert.Equal(0, fixture.Transport.DownloadRequests);
    }

    [Fact]
    public async Task Manual_upload_can_still_replace_a_task_backup_with_an_empty_local_list()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.Tasks.Tasks.Clear();
        fixture.Transport.LatestResult =
            OperationResult<BackupSnapshotDto?>.Success(FakeSyncTransport.Snapshot("snap-with-tasks", taskCount: 2));

        var result = await fixture.Api.UploadAsync(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, fixture.Transport.Uploads);
    }

    [Fact]
    public async Task Upload_sends_tasks_and_only_the_portable_settings_whitelist()
    {
        var fixture = await new Fixture().StartAsync(
            ("tasks.confirmDelete", SettingPersistence.UserSettings),
            ("tasks.createOnce", SettingPersistence.HostConfig),
            ("ui.text.placeholder", SettingPersistence.UserSettings),
            ("appearance.theme", SettingPersistence.UserSettings),
            ("ui.language", SettingPersistence.UserSettings),
            ("desktop.presence.runAtLogon", SettingPersistence.UserSettings),
            ("sync.enabled", SettingPersistence.UserSettings),
            ("auth.serverUrl", SettingPersistence.UserSettings),
            ("host.modules", SettingPersistence.HostConfig),
            ("storage.filename", SettingPersistence.HostConfig));
        fixture.AddTask();
        fixture.Settings.Values["tasks.confirmDelete"] = true;
        fixture.Settings.Values["tasks.createOnce"] = true;
        fixture.Settings.Values["ui.text.placeholder"] = "写点什么";
        fixture.Settings.Values["appearance.theme"] = "Dark";
        fixture.Settings.Values["ui.language"] = "Chinese";
        fixture.Settings.Values["desktop.presence.runAtLogon"] = true;
        fixture.Settings.Values["sync.enabled"] = true;
        fixture.Settings.Values["auth.serverUrl"] = "https://sync.example.com";
        fixture.Settings.Values["host.modules"] = new[] { "tasks-core" };
        fixture.Settings.Values["storage.filename"] = "mosaic.sqlite";

        var result = await fixture.Api.UploadAsync(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var keys = fixture.Transport.LastUpload!.Select(item => item.Key).ToArray();
        Assert.Equal(
            new[] { TaskId.ToString("D"), "appearance.theme", "tasks.confirmDelete", "ui.language" },
            keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(1, result.Value.TaskCount);
        Assert.Equal(3, result.Value.SettingCount);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), fixture.Transport.LastDeviceId);
        Assert.Equal("access-token", fixture.Transport.LastToken);
        var taskItem = fixture.Transport.LastUpload!.Single(item => item.Kind == SyncItemKinds.Task);
        Assert.Equal(7, taskItem.Revision);
        Assert.Contains("archivedAtUtc", taskItem.Payload, StringComparison.Ordinal);
        // Nothing that belongs to this machine, this account or the settings journal travels.
        Assert.DoesNotContain(keys, key => key.StartsWith("host.", StringComparison.Ordinal)
            || key.StartsWith("storage.", StringComparison.Ordinal)
            || key.StartsWith("desktop.", StringComparison.Ordinal)
            || key.StartsWith("sync.", StringComparison.Ordinal)
            || key.StartsWith("auth.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upload_never_sends_host_specific_task_settings_that_match_the_old_prefixes()
    {
        // tasks.revisionStart and tasks.floating.allowNotesEditing used to slip through the
        // "tasks." prefix match; server 0.1.1 would reject the whole backup with 400 backup.invalid.
        var fixture = await new Fixture().StartAsync(
            ("tasks.revisionStart", SettingPersistence.UserSettings),
            ("tasks.floating.allowNotesEditing", SettingPersistence.UserSettings),
            ("tasks.sortOrder", SettingPersistence.UserSettings));
        fixture.Settings.Values["tasks.revisionStart"] = 5L;
        fixture.Settings.Values["tasks.floating.allowNotesEditing"] = false;
        fixture.Settings.Values["tasks.sortOrder"] = "DueDate";
        fixture.AddTask();

        var result = await fixture.Api.UploadAsync(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var keys = fixture.Transport.LastUpload!.Where(item => item.Kind == SyncItemKinds.Setting).Select(item => item.Key).ToArray();
        Assert.Equal(new[] { "tasks.sortOrder" }, keys);
        Assert.Equal(1, result.Value.SettingCount);
    }

    [Fact]
    public async Task Upload_sends_exactly_the_fourteen_whitelisted_settings_when_all_are_defined()
    {
        string[] whitelist =
        [
            "ui.language", "appearance.theme", "tasks.sortOrder", "tasks.defaultFilter",
            "tasks.archive.autoEnabled", "tasks.archive.delayDays", "tasks.title.maxCharacters",
            "tasks.notes.maxCharacters", "tasks.title.required", "tasks.defaultCompleted",
            "tasks.confirmDelete", "tasks.confirmUnsaved", "tasks.showCharacterCount",
            "tasks.autoSwitchToAllOnHidden"
        ];
        var fixture = await new Fixture().StartAsync(whitelist.Select(id => (id, SettingPersistence.UserSettings)).ToArray());
        foreach (var id in whitelist) fixture.Settings.Values[id] = "value-" + id;
        fixture.AddTask();

        var result = await fixture.Api.UploadAsync(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var keys = fixture.Transport.LastUpload!.Where(item => item.Kind == SyncItemKinds.Setting).Select(item => item.Key).ToArray();
        Assert.Equal(whitelist.OrderBy(key => key, StringComparer.Ordinal), keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(14, result.Value.SettingCount);
    }

    [Fact]
    public async Task Upload_can_omit_settings_for_a_tasks_only_backup()
    {
        var fixture = await new Fixture().StartAsync(("appearance.theme", SettingPersistence.UserSettings));
        fixture.Settings.Values["appearance.theme"] = "Light";
        fixture.AddTask();

        var result = await fixture.Api.UploadAsync(false);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.SettingCount);
        Assert.All(fixture.Transport.LastUpload!, item => Assert.Equal(SyncItemKinds.Task, item.Kind));
    }

    [Fact]
    public async Task Include_settings_option_overrides_the_per_upload_choice()
    {
        var fixture = await new Fixture().StartAsync(
            ("sync.includeSettings", SettingPersistence.UserSettings),
            ("appearance.theme", SettingPersistence.UserSettings));
        fixture.Settings.Values["sync.includeSettings"] = false;
        fixture.Settings.Values["appearance.theme"] = "Dark";
        await fixture.Settings.ApplySourcesAsync();
        fixture.AddTask();

        var result = await fixture.Api.UploadAsync(true);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.SettingCount);
        Assert.All(fixture.Transport.LastUpload!, item => Assert.Equal(SyncItemKinds.Task, item.Kind));
    }

    [Fact]
    public async Task Refresh_latest_records_the_remote_snapshot_in_state()
    {
        var fixture = await new Fixture().StartAsync();

        var result = await fixture.Api.RefreshLatestAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("snap-1", result.Value!.SnapshotId);
        Assert.Equal("snap-1", fixture.Api.State.LatestSnapshot!.SnapshotId);
        Assert.Equal("access-token", fixture.Transport.LastToken);
    }

    [Fact]
    public async Task Download_replaces_tasks_and_applies_only_portable_backup_settings()
    {
        var fixture = await new Fixture().StartAsync(
            ("appearance.theme", SettingPersistence.UserSettings),
            ("ui.language", SettingPersistence.UserSettings),
            ("sync.enabled", SettingPersistence.UserSettings),
            ("auth.serverUrl", SettingPersistence.UserSettings),
            ("host.modules", SettingPersistence.HostConfig));
        var backupTask = new TaskItemDto(TaskId, "备份任务", "备注", true,
            new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), 12);
        fixture.Transport.DownloadResult = OperationResult<BackupSnapshotContentDto>.Success(new(
            FakeSyncTransport.Snapshot("snap-1"),
            [
                new(SyncItemKinds.Task, TaskId.ToString("D"), JsonSerializer.Serialize(backupTask), 12),
                new(SyncItemKinds.Setting, "appearance.theme", SettingPayload("Dark"), 1),
                new(SyncItemKinds.Setting, "ui.language", SettingPayload("English"), 1),
                new(SyncItemKinds.Setting, "sync.enabled", SettingPayload(false), 1),
                new(SyncItemKinds.Setting, "auth.serverUrl", SettingPayload("https://evil.example.com"), 1),
                new(SyncItemKinds.Setting, "host.modules", SettingPayload(new[] { "tasks-core" }), 1)
            ]));

        var result = await fixture.Api.DownloadAndReplaceAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new RestoreSummary(1, 2), result.Value);
        var restored = Assert.Single(fixture.Tasks.Replaced!);
        Assert.Equal(TaskId, restored.Id);
        Assert.Equal(12, restored.Revision);
        Assert.True(restored.IsCompleted);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), restored.UpdatedAtUtc);
        var commit = Assert.Single(fixture.Settings.Commits);
        Assert.Equal(new[] { "appearance.theme", "ui.language" }, commit.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal("Dark", commit["appearance.theme"]);
        Assert.False(fixture.Settings.Values.ContainsKey("sync.enabled"));
        Assert.Equal(new RestoreSummary(1, 2), fixture.Api.State.LastRestore);
    }

    [Fact]
    public async Task Download_without_a_remote_snapshot_reports_an_invalid_snapshot()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.Transport.LatestResult = OperationResult<BackupSnapshotDto?>.Success(null);

        var result = await fixture.Api.DownloadAndReplaceAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(SyncErrorCodes.SnapshotInvalid, result.Error!.Code);
        Assert.Null(fixture.Tasks.Replaced);
    }

    [Fact]
    public async Task Download_rejects_a_backup_whose_task_payload_cannot_be_read()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.Transport.DownloadResult = OperationResult<BackupSnapshotContentDto>.Success(new(
            FakeSyncTransport.Snapshot("snap-1"), [new(SyncItemKinds.Task, TaskId.ToString("D"), "{not json", 1)]));

        var result = await fixture.Api.DownloadAndReplaceAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(SyncErrorCodes.SnapshotInvalid, result.Error!.Code);
        Assert.Null(fixture.Tasks.Replaced);
        Assert.Empty(fixture.Settings.Commits);
    }

    [Fact]
    public async Task Download_reports_a_task_import_failure_and_leaves_the_restore_state_empty()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.Tasks.ReplaceResult = OperationResult.Failure(new(ErrorCodes.InternalError, "磁盘已满"));
        fixture.Transport.DownloadResult = OperationResult<BackupSnapshotContentDto>.Success(new(
            FakeSyncTransport.Snapshot("snap-1"), [new(SyncItemKinds.Setting, "ui.language", SettingPayload("English"), 1)]));
        fixture.Settings.Definitions.Add(FakeSettings.Definition("ui.language", SettingPersistence.UserSettings));

        var result = await fixture.Api.DownloadAndReplaceAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(SyncErrorCodes.TasksImportFailed, result.Error!.Code);
        Assert.Null(fixture.Api.State.LastRestore);
        Assert.Empty(fixture.Settings.Commits);
    }

    private static readonly Guid AttachmentKept = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid AttachmentFresh = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid AttachmentStale = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid AttachmentOrphanTask = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTimeOffset AttachmentAddedAt = new(2026, 11, 18, 7, 0, 0, TimeSpan.Zero);
    private static readonly byte[] AttachmentBytes = "附件一"u8.ToArray();
    private static readonly byte[] StaleAttachmentBytes = "旧附件"u8.ToArray();
    private static readonly byte[] OrphanAttachmentBytes = "孤儿附件"u8.ToArray();

    private static TaskItemDto BackupTask() => new(TaskId, "备份任务", "", false,
        new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), 12);

    private static OperationResult<BackupSnapshotContentDto> TaskBackup() =>
        OperationResult<BackupSnapshotContentDto>.Success(new(
            FakeSyncTransport.Snapshot("snap-1"),
            [new(SyncItemKinds.Task, TaskId.ToString("D"), JsonSerializer.Serialize(BackupTask()), 12)]));

    private static TaskAttachmentDto LocalAttachment(Guid id, Guid taskId, string fileName, byte[] content) =>
        new(id, taskId, fileName, content.Length, AttachmentAddedAt);

    [Fact]
    public async Task Upload_mirrors_local_attachments_and_deletes_the_ones_missing_locally()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.AddTask();
        fixture.Tasks.Attachments.Add(LocalAttachment(AttachmentKept, TaskId, "kept.txt", AttachmentBytes));
        fixture.Tasks.Blobs[AttachmentKept] = AttachmentBytes;
        fixture.Tasks.Attachments.Add(LocalAttachment(AttachmentFresh, TaskId, "fresh.txt", AttachmentBytes));
        fixture.Tasks.Blobs[AttachmentFresh] = AttachmentBytes;
        // 服务器上已有 kept 的同内容副本与一份本地已删除的 stale。
        fixture.Transport.AddRemoteAttachment(AttachmentKept, TaskId, "kept.txt", AttachmentBytes);
        fixture.Transport.AddRemoteAttachment(AttachmentStale, TaskId, "stale.txt", StaleAttachmentBytes);

        var result = await fixture.Api.UploadAsync(false);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new[] { AttachmentFresh }, fixture.Transport.UploadedAttachments);
        Assert.Equal(new[] { AttachmentStale }, fixture.Transport.DeletedAttachments);
        Assert.DoesNotContain(fixture.Transport.RemoteAttachments, item => item.Id == AttachmentStale);
        Assert.Equal(2, result.Value.AttachmentCount);
        Assert.Equal(1, result.Value.DeletedAttachmentCount);
        Assert.Equal(1, result.Value.TaskCount);
    }

    [Fact]
    public async Task Upload_reports_a_remote_attachment_failure_after_the_snapshot_is_already_published()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.AddTask();
        fixture.Tasks.Attachments.Add(LocalAttachment(AttachmentFresh, TaskId, "fresh.txt", AttachmentBytes));
        fixture.Tasks.Blobs[AttachmentFresh] = AttachmentBytes;
        fixture.Transport.UploadAttachmentResult = OperationResult.Failure(
            new(SyncErrorCodes.NetworkUnavailable, "无法连接服务器。"));

        var result = await fixture.Api.UploadAsync(false);

        Assert.False(result.IsSuccess);
        Assert.Equal(SyncErrorCodes.NetworkUnavailable, result.Error!.Code);
        Assert.Contains("fresh.txt", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Transport.Uploads);
        Assert.Equal("snap-1", fixture.Api.State.LatestSnapshot!.SnapshotId);
    }

    [Fact]
    public async Task Upload_leaves_the_server_manifest_alone_when_attachment_sync_is_off()
    {
        var fixture = await new Fixture().StartAsync(("sync.includeAttachments", SettingPersistence.UserSettings));
        fixture.AddTask();
        fixture.Settings.Values["sync.includeAttachments"] = false;
        await fixture.Settings.ApplySourcesAsync();
        fixture.Tasks.Attachments.Add(LocalAttachment(AttachmentFresh, TaskId, "fresh.txt", AttachmentBytes));
        fixture.Tasks.Blobs[AttachmentFresh] = AttachmentBytes;
        fixture.Transport.AddRemoteAttachment(AttachmentStale, TaskId, "stale.txt", StaleAttachmentBytes);

        var result = await fixture.Api.UploadAsync(false);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(0, result.Value.AttachmentCount);
        Assert.Equal(0, result.Value.DeletedAttachmentCount);
        Assert.Empty(fixture.Transport.UploadedAttachments);
        Assert.Empty(fixture.Transport.DeletedAttachments);
        Assert.Equal(0, fixture.Transport.ListAttachmentRequests);
    }

    [Fact]
    public async Task Download_restores_only_attachments_of_backed_up_tasks_and_prunes_other_local_rows()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.Transport.DownloadResult = TaskBackup();
        fixture.Transport.AddRemoteAttachment(AttachmentKept, TaskId, "kept.txt", AttachmentBytes, AttachmentAddedAt);
        // 服务器上属于"备份里没有的任务"的附件不得落到本机。
        fixture.Transport.AddRemoteAttachment(AttachmentOrphanTask, Guid.NewGuid(), "orphan.txt", OrphanAttachmentBytes);
        fixture.Tasks.Attachments.Add(LocalAttachment(AttachmentStale, TaskId, "stale.txt", StaleAttachmentBytes));
        fixture.Tasks.Blobs[AttachmentStale] = StaleAttachmentBytes;

        var result = await fixture.Api.DownloadAndReplaceAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new RestoreSummary(1, 0, 1), result.Value);
        Assert.Equal(new[] { AttachmentKept }, fixture.Transport.DownloadedAttachments);
        Assert.Equal(new[] { AttachmentKept }, fixture.Tasks.ImportedAttachments);
        var imported = Assert.Single(fixture.Tasks.Attachments);
        Assert.Equal(AttachmentAddedAt, imported.AddedAtUtc);
        Assert.Equal(AttachmentBytes, fixture.Tasks.Blobs[AttachmentKept]);
        Assert.Equal(1, fixture.Tasks.Sweeps);
        Assert.Equal(new RestoreSummary(1, 0, 1), fixture.Api.State.LastRestore);
    }

    [Fact]
    public async Task Download_keeps_restoring_the_other_attachments_and_reports_the_first_failure()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.Transport.DownloadResult = TaskBackup();
        fixture.Transport.AddRemoteAttachment(AttachmentStale, TaskId, "stale.txt", StaleAttachmentBytes);
        fixture.Transport.AddRemoteAttachment(AttachmentKept, TaskId, "kept.txt", AttachmentBytes);
        fixture.Transport.FailDownload.Add(AttachmentStale);

        var result = await fixture.Api.DownloadAndReplaceAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(SyncErrorCodes.NetworkUnavailable, result.Error!.Code);
        Assert.Contains("1 个附件未能恢复", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("stale.txt", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { AttachmentKept }, fixture.Tasks.ImportedAttachments);
        Assert.NotNull(fixture.Tasks.Replaced);
        Assert.Equal(new RestoreSummary(1, 0, 1), fixture.Api.State.LastRestore);
    }

    [Fact]
    public async Task Download_abandons_before_touching_local_data_when_the_manifest_cannot_be_read()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.Transport.DownloadResult = TaskBackup();
        fixture.Tasks.Attachments.Add(LocalAttachment(AttachmentStale, TaskId, "stale.txt", StaleAttachmentBytes));
        fixture.Transport.ManifestResult = OperationResult<AttachmentManifestDto>.Failure(
            new(SyncErrorCodes.NetworkUnavailable, "无法连接服务器。"));

        var result = await fixture.Api.DownloadAndReplaceAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(SyncErrorCodes.NetworkUnavailable, result.Error!.Code);
        Assert.Null(fixture.Tasks.Replaced);
        Assert.Contains(fixture.Tasks.Attachments, item => item.Id == AttachmentStale);
        Assert.Null(fixture.Api.State.LastRestore);
    }

    [Fact]
    public async Task Download_with_attachment_sync_off_restores_tasks_and_reports_zero_attachments()
    {
        var fixture = await new Fixture().StartAsync(("sync.includeAttachments", SettingPersistence.UserSettings));
        fixture.Settings.Values["sync.includeAttachments"] = false;
        await fixture.Settings.ApplySourcesAsync();
        fixture.Transport.DownloadResult = TaskBackup();
        fixture.Transport.AddRemoteAttachment(AttachmentKept, TaskId, "kept.txt", AttachmentBytes);
        fixture.Tasks.Attachments.Add(LocalAttachment(AttachmentStale, TaskId, "stale.txt", StaleAttachmentBytes));

        var result = await fixture.Api.DownloadAndReplaceAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new RestoreSummary(1, 0, 0), result.Value);
        Assert.Equal(0, fixture.Transport.ListAttachmentRequests);
        Assert.Empty(fixture.Transport.DownloadedAttachments);
        Assert.Equal(0, fixture.Tasks.Sweeps);
        // ReplaceAllAsync 的级联清空本地附件行；磁盘文件成为孤儿，需由用户手动清理。
        Assert.Empty(fixture.Tasks.Attachments);
    }

    [Fact]
    public async Task Signing_out_is_reflected_in_sync_state_and_notifies_listeners()
    {
        var fixture = await new Fixture().StartAsync();
        var changes = 0;
        fixture.Api.StateChanged += (_, _) => changes++;

        fixture.Auth.State = AuthState.SignedOut(null);
        fixture.Auth.Raise();

        Assert.False(fixture.Api.State.IsSignedIn);
        Assert.True(changes >= 1);
    }

    [Fact]
    public async Task Stopping_the_module_unregisters_its_setting_source_and_blocks_calls()
    {
        var fixture = await new Fixture().StartAsync();

        await fixture.Module!.StopAsync();

        Assert.Empty(fixture.Settings.Sources);
        Assert.Equal(ErrorCodes.CapabilityUnavailable, (await fixture.Api.UploadAsync(false)).Error!.Code);
    }
}
