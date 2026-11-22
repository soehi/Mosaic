using System.Reflection;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Core;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Core.Tests;

public sealed class AttachmentCleanupTests
{
    private static readonly Assembly Core = typeof(TasksModule).Assembly;
    private static readonly Type CleanupType = Core.GetType("Mosaic.Tasks.Core.Internal.TaskAttachmentCleanup", throwOnError: true)!;
    private static readonly Type StoreType = Core.GetType("Mosaic.Tasks.Core.Internal.TaskAttachmentStore", throwOnError: true)!;
    private static readonly Type SettingsSourceType = Core.GetType("Mosaic.Tasks.Core.Internal.TaskSettingsSource", throwOnError: true)!;
    private static readonly Type RulesType = Core.GetType("Mosaic.Tasks.Core.Internal.TaskRules", throwOnError: true)!;
    private static readonly Type AutoArchiveType = Core.GetType("Mosaic.Tasks.Core.Internal.TaskAutoArchive", throwOnError: true)!;
    private static readonly MethodInfo ApplyMethod = CleanupType.GetMethod("ApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo SetDaysMethod = CleanupType.GetMethod("SetDaysAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo CleanupMethod = CleanupType.GetMethod("CleanupDueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Diagnostics : IDiagnosticSink
    {
        internal List<(string Id, Exception Exception)> Reports { get; } = [];
        public void Report(string correlationId, Exception exception) { lock (Reports) Reports.Add((correlationId, exception)); }
    }

    private sealed class Repository : ITaskAttachmentRepository
    {
        private readonly List<TaskAttachmentDto> _items;
        internal List<DateTimeOffset> Cutoffs { get; } = [];
        internal List<Guid> Deleted { get; } = [];
        internal bool FailDelete { get; set; }
        internal Repository(params TaskAttachmentDto[] items) => _items = items.ToList();
        public Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListExpiredArchivedAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
        {
            Cutoffs.Add(cutoffUtc);
            return Task.FromResult(OperationResult<IReadOnlyList<TaskAttachmentDto>>.Success(_items));
        }
        public Task<OperationResult> DeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default)
        {
            if (FailDelete) return Task.FromResult(OperationResult.Failure(new(ErrorCodes.StorageUnavailable, "unavailable")));
            Deleted.Add(attachmentId);
            _items.RemoveAll(item => item.Id == attachmentId);
            return Task.FromResult(OperationResult.Success());
        }
        public Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListByTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> InsertAsync(TaskAttachmentDto attachment, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskAttachmentDto?>> FindAsync(Guid attachmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<long>> CountByTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public void Cleanup_setting_defaults_to_disabled_and_has_localized_display_keys()
    {
        var rules = Activator.CreateInstance(RulesType);
        var archive = Activator.CreateInstance(AutoArchiveType, new TestSupport.MemoryTaskRepository(), new StubTasksApi(), new Clock(), new Diagnostics());
        var source = (ISettingSource)Activator.CreateInstance(SettingsSourceType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [rules!, archive!], null)!;

        var setting = Assert.Single(source.Definitions, definition => definition.Id == "tasks.attachments.archiveCleanupDays");
        Assert.Equal(SettingValueKind.Integer, setting.Kind);
        Assert.Equal(0, setting.DefaultValue);
        Assert.Equal(0, setting.Min);
        Assert.Equal(365, setting.Max);
        Assert.Equal(SettingCategory.Normal, setting.Category);
        Assert.Equal(SettingApplyMode.Immediate, setting.ApplyMode);
        Assert.Equal("Settings.Setting.AttachmentArchiveCleanupDays", setting.DisplayNameKey);
    }

    [Fact]
    public async Task Enabled_cleanup_deletes_expired_records_and_files_and_disabled_policy_stops()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "attachment.txt");
        await File.WriteAllTextAsync(path, "attachment");
        var attachment = new TaskAttachmentDto(Guid.NewGuid(), Guid.NewGuid(), "attachment.txt", 10, Now.AddDays(-8)) { StoredPath = path };
        var repository = new Repository(attachment);
        var cleanup = NewCleanup(repository, directory, out _);

        await ApplyPolicy(cleanup, 7);
        Assert.Equal(7, Field(cleanup, "_days"));
        await Until(() => !File.Exists(path), "归档附件未在启用清理后删除");
        Assert.Equal([attachment.Id], repository.Deleted);
        Assert.Equal(Now.AddDays(-7), Assert.Single(repository.Cutoffs));

        await ApplyPolicy(cleanup, 0);
        Assert.Equal(0, Field(cleanup, "_days"));
        Assert.Null(Field(cleanup, "_cancellation"));
        Directory.Delete(directory);
    }

    [Fact]
    public async Task Failed_database_delete_preserves_file_and_reports_diagnostic()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "attachment.txt");
        await File.WriteAllTextAsync(path, "attachment");
        var attachment = new TaskAttachmentDto(Guid.NewGuid(), Guid.NewGuid(), "attachment.txt", 10, Now.AddDays(-8)) { StoredPath = path };
        var repository = new Repository(attachment) { FailDelete = true };
        var cleanup = NewCleanup(repository, directory, out var diagnostics);
        try
        {
            await (Task)CleanupMethod.Invoke(cleanup, [Now.AddDays(-7), CancellationToken.None])!;

            Assert.True(File.Exists(path));
            Assert.Empty(repository.Deleted);
            Assert.Equal("tasks.attachments.cleanup.delete", Assert.Single(diagnostics.Reports).Id);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static object NewCleanup(Repository repository, string directory, out Diagnostics diagnostics)
    {
        diagnostics = new Diagnostics();
        var store = Activator.CreateInstance(StoreType)!;
        StoreType.GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store,
            [new Dictionary<string, object?> { ["tasks.attachments.storagePath"] = directory }]);
        return Activator.CreateInstance(CleanupType, repository, store, new Clock(), diagnostics)!;
    }

    private static Task ApplyPolicy(object cleanup, int days) =>
        (Task)ApplyMethod.Invoke(cleanup, [new Dictionary<string, object?> { ["tasks.attachments.archiveCleanupDays"] = days }])!;

    private static object? Field(object cleanup, string name) =>
        CleanupType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cleanup);

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) Assert.Fail(message);
            await Task.Delay(20);
        }
    }

    private sealed class StubTasksApi : ITasksApi
    {
        public Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> SetArchivedAsync(SetTaskArchivedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskConstraintsDto>> GetConstraintsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
