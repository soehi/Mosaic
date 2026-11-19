using System.Reflection;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Core;
using Mosaic.TestSupport;

namespace Mosaic.Tasks.Core.Tests;

public sealed class AutoArchiveTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string correlationId, Exception exception) { } }
    private static readonly Assembly Core = typeof(TasksModule).Assembly;
    private static readonly Type AutoArchiveType = Core.GetType("Mosaic.Tasks.Core.Internal.TaskAutoArchive", throwOnError: true)!;
    private static readonly Type SettingsSourceType = Core.GetType("Mosaic.Tasks.Core.Internal.TaskSettingsSource", throwOnError: true)!;
    private static readonly Type RulesType = Core.GetType("Mosaic.Tasks.Core.Internal.TaskRules", throwOnError: true)!;
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    private static TaskItemDto TaskItem(bool completed, bool archived = false, DateTimeOffset? completedAt = null, DateTimeOffset? restoredAt = null) =>
        new(Guid.NewGuid(), "任务", "", completed, Now.AddDays(-30), Now, 1)
        {
            CompletedAtUtc = completedAt,
            ArchivedAtUtc = archived ? Now.AddDays(-10) : null,
            RestoredAtUtc = restoredAt
        };

    private static async Task<IReadOnlyList<TaskItemDto>> Candidates(MemoryTaskRepository repository, DateTimeOffset cutoff)
    {
        var result = await repository.QueryArchiveCandidatesAsync(cutoff, 64);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static Task<OperationResult> Insert(MemoryTaskRepository repository, TaskItemDto task) =>
        repository.InsertAsync(task);

    [Fact]
    public async Task Open_tasks_are_never_archive_candidates_no_matter_how_old()
    {
        var repository = new MemoryTaskRepository();
        var openAncient = TaskItem(false, completedAt: Now.AddDays(-365), restoredAt: Now.AddDays(-365));
        var openNoDates = TaskItem(false);
        Assert.True((await Insert(repository, openAncient)).IsSuccess);
        Assert.True((await Insert(repository, openNoDates)).IsSuccess);

        Assert.Empty(await Candidates(repository, Now));
        Assert.Empty(await Candidates(repository, Now.AddYears(10)));
    }

    [Fact]
    public async Task Completed_tasks_only_after_the_configured_waiting_period()
    {
        var repository = new MemoryTaskRepository();
        var due = TaskItem(true, completedAt: Now.AddDays(-8));
        var fresh = TaskItem(true, completedAt: Now.AddDays(-6));
        Assert.True((await Insert(repository, due)).IsSuccess);
        Assert.True((await Insert(repository, fresh)).IsSuccess);
        var cutoff = Now.AddDays(-7);

        Assert.Equal(due.Id, Assert.Single(await Candidates(repository, cutoff)).Id);
    }

    [Fact]
    public async Task Already_archived_tasks_are_not_candidates_again()
    {
        var repository = new MemoryTaskRepository();
        var archived = TaskItem(true, archived: true, completedAt: Now.AddDays(-30));
        Assert.True((await Insert(repository, archived)).IsSuccess);

        Assert.Empty(await Candidates(repository, Now));
    }

    [Fact]
    public async Task Restored_tasks_restart_the_waiting_period_from_the_restore_time()
    {
        var repository = new MemoryTaskRepository();
        var restored = TaskItem(true, completedAt: Now.AddDays(-60), restoredAt: Now.AddDays(-3));
        Assert.True((await Insert(repository, restored)).IsSuccess);
        var cutoff = Now.AddDays(-7);

        Assert.Empty(await Candidates(repository, cutoff));
        Assert.Equal(restored.Id, Assert.Single(await Candidates(repository, cutoff.AddDays(5))).Id);
    }

    [Fact]
    public void Auto_archive_settings_default_to_disabled_and_document_the_completed_only_policy()
    {
        var rules = Activator.CreateInstance(RulesType);
        var archive = NewArchive(new MemoryTaskRepository(), new StubTasksApi());
        var source = (ISettingSource)Activator.CreateInstance(SettingsSourceType, rules!, archive)!;

        var autoEnabled = Assert.Single(source.Definitions, definition => definition.Id == "tasks.archive.autoEnabled");
        var delayDays = Assert.Single(source.Definitions, definition => definition.Id == "tasks.archive.delayDays");

        Assert.Equal(SettingValueKind.Boolean, autoEnabled.Kind);
        Assert.False(Assert.IsType<bool>(autoEnabled.DefaultValue));
        Assert.Contains("已完成", autoEnabled.Description);
        Assert.Contains("未完成", autoEnabled.Description);
        Assert.Contains("关闭", autoEnabled.Description);
        Assert.Equal("Settings.Setting.AutoArchive", autoEnabled.DisplayNameKey);
        Assert.Equal("Settings.Setting.AutoArchive.Description", autoEnabled.DescriptionKey);
        Assert.Equal(7, Assert.IsType<int>(delayDays.DefaultValue));
    }

    [Fact]
    public async Task Auto_archive_archive_pass_only_writes_tasks_returned_by_the_candidates_query()
    {
        var repository = new MemoryTaskRepository();
        var due = TaskItem(true, completedAt: Now.AddDays(-8));
        var open = TaskItem(false, completedAt: Now.AddDays(-365));
        Assert.True((await Insert(repository, due)).IsSuccess);
        Assert.True((await Insert(repository, open)).IsSuccess);
        var api = new PassThroughTasksApi(repository);
        var archive = NewArchive(repository, api);
        var cutoff = Now.AddDays(-7);

        var pass = AutoArchiveType.GetMethod("ArchiveDueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)pass.Invoke(archive, [cutoff, CancellationToken.None])!;

        Assert.Equal([due.Id], api.ArchiveRequests.Select(request => request.Id).ToArray());
        Assert.All(api.ArchiveRequests, request => Assert.True(request.IsArchived));
        var storedDue = await repository.FindAsync(due.Id);
        Assert.True(storedDue.Value is { IsArchived: true });
        var storedOpen = await repository.FindAsync(open.Id);
        Assert.True(storedOpen.Value is { IsArchived: false });
    }

    private static object NewArchive(MemoryTaskRepository repository, ITasksApi api) =>
        Activator.CreateInstance(AutoArchiveType, repository, api, new Clock(), new Diagnostics())!;

    private sealed class StubTasksApi : ITasksApi
    {
        public Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> SetArchivedAsync(SetTaskArchivedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException("disabled policy must never archive");
        public Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskConstraintsDto>> GetConstraintsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PassThroughTasksApi(MemoryTaskRepository repository) : ITasksApi
    {
        internal List<SetTaskArchivedRequest> ArchiveRequests { get; } = [];
        public Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<OperationResult<TaskItemDto>> SetArchivedAsync(SetTaskArchivedRequest request, CancellationToken cancellationToken = default)
        {
            ArchiveRequests.Add(request);
            var found = await repository.FindAsync(request.Id, cancellationToken);
            if (found.Value is not { } current || current.Revision != request.ExpectedRevision)
                return OperationResult<TaskItemDto>.Failure(new(ErrorCodes.Conflict, "Conflict"));
            var next = current with
            {
                ArchivedAtUtc = request.IsArchived ? Now : null,
                RestoredAtUtc = request.IsArchived ? current.RestoredAtUtc : Now,
                Revision = current.Revision + 1
            };
            var saved = await repository.ReplaceAsync(next, current.Revision, cancellationToken);
            return saved.IsSuccess ? OperationResult<TaskItemDto>.Success(next) : OperationResult<TaskItemDto>.Failure(saved.Error!);
        }
        public Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskConstraintsDto>> GetConstraintsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
