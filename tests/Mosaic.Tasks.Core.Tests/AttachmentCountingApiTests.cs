using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Core;
using Mosaic.Tasks.Persistence.Contracts;
using Mosaic.TestSupport;

namespace Mosaic.Tasks.Core.Tests;

// 计数装饰器：主列表、速览与浮窗的 DTO 必须携带最新 AttachmentCount；
// 批量计数用一条查询完成，失败时回退为 0 且不阻断任务操作。
public sealed class AttachmentCountingApiTests
{
    private sealed class Gate : IModuleExecutionGate, IDisposable
    {
        public void Activate() { }
        public IDisposable? TryEnter() => this;
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Ids : IIdGenerator { public Guid NewId() => Guid.NewGuid(); }
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string correlationId, Exception exception) { } }

    private sealed class CountingRepository(Dictionary<Guid, int> counts) : ITaskAttachmentRepository
    {
        internal int BatchQueries { get; private set; }
        internal int SingleQueries { get; private set; }
        internal bool Fail { get; set; }

        public Task<OperationResult<long>> CountByTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            SingleQueries++;
            return Task.FromResult(Fail
                ? OperationResult<long>.Failure(new(ErrorCodes.StorageUnavailable, "unavailable"))
                : OperationResult<long>.Success(counts.GetValueOrDefault(taskId)));
        }

        public Task<OperationResult<IReadOnlyDictionary<Guid, long>>> CountsByTasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken = default)
        {
            BatchQueries++;
            if (Fail) return Task.FromResult(OperationResult<IReadOnlyDictionary<Guid, long>>.Failure(new(ErrorCodes.StorageUnavailable, "unavailable")));
            IReadOnlyDictionary<Guid, long> result = taskIds.Where(counts.ContainsKey).ToDictionary(id => id, id => (long)counts[id]);
            return Task.FromResult(OperationResult<IReadOnlyDictionary<Guid, long>>.Success(result));
        }

        public Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListByTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> InsertAsync(TaskAttachmentDto attachment, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskAttachmentDto?>> FindAsync(Guid attachmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> DeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListOrphanedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListExpiredArchivedAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static async Task<(TasksModule Module, CountingRepository Counts)> Start(Dictionary<Guid, int> counts)
    {
        var repository = new CountingRepository(counts);
        var module = new TasksModule(new(new MemoryTaskRepository(), new Clock(), new Ids(), new Gate(), new Diagnostics(), AttachmentRepository: repository));
        await module.ActivateAsync();
        return (module, repository);
    }

    [Fact]
    public async Task List_uses_one_batch_query_and_stamps_counts_per_task()
    {
        var counts = new Dictionary<Guid, int>();
        var (module, repository) = await Start(counts);
        var withAttachments = (await module.Api.CreateAsync(new("带附件", ""))).Value;
        var without = (await module.Api.CreateAsync(new("无附件", ""))).Value;
        counts[withAttachments.Id] = 3;

        var result = await module.Api.ListAsync(new(TaskFilter.All));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(3, result.Value.Single(task => task.Id == withAttachments.Id).AttachmentCount);
        Assert.True(result.Value.Single(task => task.Id == withAttachments.Id).HasAttachments);
        Assert.Equal(0, result.Value.Single(task => task.Id == without.Id).AttachmentCount);
        Assert.False(result.Value.Single(task => task.Id == without.Id).HasAttachments);
        Assert.Equal(1, repository.BatchQueries);
        var singleQueriesBeforeList = 2; // 两次 Create 各一次单任务计数
        Assert.Equal(singleQueriesBeforeList, repository.SingleQueries);
        await module.DisposeAsync();
    }

    [Fact]
    public async Task Get_and_update_stamp_count_and_fallback_to_zero_on_failure()
    {
        var counts = new Dictionary<Guid, int>();
        var repository = new CountingRepository(counts);
        var module = new TasksModule(new(new MemoryTaskRepository(), new Clock(), new Ids(), new Gate(), new Diagnostics(), AttachmentRepository: repository));
        await module.ActivateAsync();
        var created = (await module.Api.CreateAsync(new("任务", ""))).Value;
        counts[created.Id] = 2;

        var fetched = (await module.Api.GetAsync(created.Id)).Value;
        Assert.Equal(2, fetched.AttachmentCount);
        Assert.True(fetched.HasAttachments);

        var updated = (await module.Api.UpdateAsync(new(created.Id, "改名", "", created.Revision))).Value;
        Assert.Equal(2, updated.AttachmentCount);

        repository.Fail = true;
        var fallback = (await module.Api.GetAsync(created.Id)).Value;
        Assert.Equal(0, fallback.AttachmentCount);
        await module.DisposeAsync();
    }
}
