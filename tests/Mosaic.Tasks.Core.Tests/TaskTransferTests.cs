using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Core;
using Mosaic.Tasks.Persistence.Contracts;
using Mosaic.TestSupport;

namespace Mosaic.Tasks.Core.Tests;

// Transfer is the backup/restore seam: export must observe the same listing rules as the UI
// (including archived tasks) and replace must validate every row before the single-transaction swap.
public sealed class TaskTransferTests
{
    private sealed class Gate : IModuleExecutionGate, IDisposable
    {
        private bool _active;
        public void Activate() => _active = true;
        public IDisposable? TryEnter() => _active ? this : null;
        public Task StopAsync() { _active = false; return Task.CompletedTask; }
        public void Dispose() { }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Ids : IIdGenerator { public Guid NewId() => Guid.NewGuid(); }
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string correlationId, Exception exception) { } }

    // Models the real single-transaction swap: on success the table only contains the incoming rows.
    private sealed class FakeTransferRepository(MemoryTaskRepository backing) : ITaskTransferRepository
    {
        public IReadOnlyList<TaskItemDto>? Replaced { get; private set; }
        public OperationResult Result { get; set; } = OperationResult.Success();
        public async Task<OperationResult> ReplaceAllAsync(IReadOnlyList<TaskItemDto> tasks, CancellationToken cancellationToken = default)
        {
            if (!Result.IsSuccess) return Result; // a failing repository never commits the swap
            Replaced = tasks;
            var existing = (await backing.QueryAsync(new TaskQuery(TaskFilter.All) { ArchiveScope = TaskArchiveScope.All })).Value!;
            foreach (var item in existing)
                await backing.DeleteAsync(item.Id, item.Revision, cancellationToken);
            foreach (var item in tasks)
                await backing.InsertAsync(item, cancellationToken);
            return OperationResult.Success();
        }
    }

    private static async Task<(TasksModule Module, MemoryTaskRepository Repository, FakeTransferRepository Transfer)> Start()
    {
        var repository = new MemoryTaskRepository();
        var transfer = new FakeTransferRepository(repository);
        var module = new TasksModule(new(repository, new Clock(), new Ids(), new Gate(), new Diagnostics(), TransferRepository: transfer));
        await module.ActivateAsync();
        return (module, repository, transfer);
    }

    private static TaskItemDto BackupTask(Guid id) => new(
        id, "备份任务", "备注", true,
        new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero), 12)
    {
        ArchivedAtUtc = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero)
    };

    // Change notifications are published on the thread pool, so give them a moment to land.
    private static async Task WaitForChangesAsync(List<TaskChange> changes, int expected)
    {
        for (var attempt = 0; attempt < 50 && changes.Count < expected; attempt++)
            await Task.Delay(20);
    }

    [Fact]
    public async Task Export_returns_every_task_including_archived_ones()
    {
        var (module, repository, _) = await Start();
        var open = (await module.Api.CreateAsync(new("进行中", ""))).Value;
        var archived = (await module.Api.CreateAsync(new("已归档", ""))).Value;
        await module.Api.SetCompletedAsync(new(archived.Id, true, 1));
        var done = (await module.Api.GetAsync(archived.Id)).Value;
        // Archive through the repository the same way the auto-archive loop does.
        await repository.ReplaceAsync(done with { ArchivedAtUtc = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero) }, done.Revision);

        var result = await module.Transfer!.ExportAllAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            new[] { open.Id, archived.Id }.OrderBy(t => t).ToArray(),
            result.Value.Select(t => t.Id).OrderBy(t => t).ToArray());
        Assert.Contains(result.Value, t => t.IsArchived && t.ArchivedAtUtc is not null);
    }

    [Fact]
    public async Task Replace_rejects_invalid_rows_before_touching_the_repository()
    {
        var (module, _, transfer) = await Start();
        var task = BackupTask(Guid.Parse("33333333-3333-3333-3333-333333333333"));

        var duplicate = await module.Transfer!.ReplaceAllAsync([task, task]);
        var emptyId = await module.Transfer.ReplaceAllAsync([task with { Id = Guid.Empty }]);
        var badRevision = await module.Transfer.ReplaceAllAsync([task with { Revision = 0 }]);
        var badTimes = await module.Transfer.ReplaceAllAsync([task with { UpdatedAtUtc = task.CreatedAtUtc.AddSeconds(-1) }]);
        var nullTitle = await module.Transfer.ReplaceAllAsync([task with { Title = null! }]);

        Assert.Equal(ErrorCodes.ValidationFailed, duplicate.Error!.Code);
        Assert.Equal(ErrorCodes.ValidationFailed, emptyId.Error!.Code);
        Assert.Equal(ErrorCodes.ValidationFailed, badRevision.Error!.Code);
        Assert.Equal(ErrorCodes.ValidationFailed, badTimes.Error!.Code);
        Assert.Equal(ErrorCodes.ValidationFailed, nullTitle.Error!.Code);
        Assert.Null(transfer.Replaced); // no invalid row ever reached the repository
        var empty = await module.Transfer.ReplaceAllAsync([]);
        Assert.True(empty.IsSuccess);
        Assert.Empty(transfer.Replaced!);
    }

    [Fact]
    public async Task Replace_all_repository_failure_is_reported_without_change_notifications()
    {
        var (module, repository, transfer) = await Start();
        var created = (await module.Api.CreateAsync(new("本地任务", ""))).Value;
        var changes = new List<TaskChange>();
        module.Changes.Subscribe(change => changes.Add(change));
        transfer.Result = OperationResult.Failure(new(ErrorCodes.InternalError, "磁盘已满"));

        var result = await module.Transfer!.ReplaceAllAsync([BackupTask(Guid.Parse("44444444-4444-4444-4444-444444444444"))]);

        Assert.Equal(ErrorCodes.InternalError, result.Error!.Code);
        Assert.Null(transfer.Replaced);
        Assert.Empty(changes);
        await WaitForChangesAsync(changes, 0);
        Assert.NotNull((await module.Api.GetAsync(created.Id)).Value);
    }

    [Fact]
    public async Task Replace_swaps_tasks_preserving_ids_and_publishes_one_bulk_change()
    {
        var (module, repository, transfer) = await Start();
        var created = (await module.Api.CreateAsync(new("旧任务", ""))).Value;
        var changes = new List<TaskChange>();
        module.Changes.Subscribe(change => changes.Add(change));
        var backup = BackupTask(Guid.Parse("55555555-5555-5555-5555-555555555555"));

        var result = await module.Transfer!.ReplaceAllAsync([backup]);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(transfer.Replaced);
        Assert.Equal(backup.Id, Assert.Single(transfer.Replaced).Id);
        Assert.Equal(12, Assert.Single(transfer.Replaced).Revision);
        await WaitForChangesAsync(changes, 1);
        // 整表恢复只发一次批量通知：观察者不必为每一行各重载一次列表。
        Assert.Equal(new TaskChange(Guid.Empty, 0, TaskChangeKind.Replaced), Assert.Single(changes));
        var oldLookup = await module.Api.GetAsync(created.Id);
        Assert.True(oldLookup.IsSuccess is false || oldLookup.Value is null, "old task should be gone after the swap");
        Assert.Equal("备份任务", (await module.Api.GetAsync(backup.Id)).Value!.Title);
        Assert.True((await module.Api.GetAsync(backup.Id)).Value!.IsArchived);
    }

    [Fact]
    public async Task Replace_rejects_a_row_exceeding_the_title_limit()
    {
        var (module, _, transfer) = await Start();

        var result = await module.Transfer!.ReplaceAllAsync(
            [BackupTask(Guid.Parse("66666666-6666-6666-6666-666666666666")) with { Title = new string('长', TaskConstraints.TitleMaxCharacters + 1) }]);

        Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.Null(transfer.Replaced);
    }
}
