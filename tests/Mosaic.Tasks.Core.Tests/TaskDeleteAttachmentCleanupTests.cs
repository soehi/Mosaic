using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Core;
using Mosaic.Tasks.Persistence.Contracts;
using Mosaic.TestSupport;

namespace Mosaic.Tasks.Core.Tests;

public sealed class TaskDeleteAttachmentCleanupTests
{
    private sealed class Gate : IModuleExecutionGate, IDisposable
    {
        private bool _active;
        public void Activate() => _active = true;
        public IDisposable? TryEnter() => _active ? this : null;
        public Task StopAsync() { _active = false; return Task.CompletedTask; }
        public void Dispose() { }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Ids : IIdGenerator { public Guid NewId() => Guid.NewGuid(); }
    private sealed class Diagnostics : IDiagnosticSink
    {
        public List<(string Id, Exception Exception)> Reports { get; } = [];
        public void Report(string correlationId, Exception exception) { lock (Reports) Reports.Add((correlationId, exception)); }
    }

    private sealed class MemoryAttachmentRepository : ITaskAttachmentRepository
    {
        private readonly List<TaskAttachmentDto> _items = [];
        private readonly Dictionary<Guid, string> _paths = [];
        public Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListByTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<TaskAttachmentDto>>.Success(_items.Where(a => a.TaskId == taskId).ToArray()));
        public Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<TaskAttachmentDto>>.Success(
                _items.OrderBy(a => a.AddedAtUtc).ThenBy(a => a.Id).ToArray()));
        public Task<OperationResult> InsertAsync(TaskAttachmentDto attachment, string storedPath, CancellationToken cancellationToken = default)
        {
            _items.Add(attachment);
            _paths[attachment.Id] = storedPath;
            return Task.FromResult(OperationResult.Success());
        }
        public Task<OperationResult<string?>> FindStoredPathAsync(Guid attachmentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<string?>.Success(_paths.TryGetValue(attachmentId, out var path) ? path : null));
        public Task<OperationResult<TaskAttachmentDto?>> FindAsync(Guid attachmentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<TaskAttachmentDto?>.Success(_items.FirstOrDefault(a => a.Id == attachmentId)));
        public Task<OperationResult> DeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default)
        {
            _items.RemoveAll(a => a.Id == attachmentId);
            return Task.FromResult(OperationResult.Success());
        }
        public Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListExpiredArchivedAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<OperationResult<long>> CountByTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<long>.Success(_items.Count(a => a.TaskId == taskId)));
        public Task<OperationResult<IReadOnlyDictionary<Guid, long>>> CountsByTasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<Guid, long> counts = taskIds.Distinct().ToDictionary(id => id, id => (long)_items.Count(a => a.TaskId == id)).Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => pair.Value);
            return Task.FromResult(OperationResult<IReadOnlyDictionary<Guid, long>>.Success(counts));
        }
    }

    [Fact]
    public async Task Deleting_task_removes_stored_attachment_copies_and_keeps_source_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(Path.GetTempPath(), $"attachment-source-{Guid.NewGuid():D}.txt");
            await File.WriteAllTextAsync(source, "content");

            var attachments = new MemoryAttachmentRepository();
            var module = new TasksModule(new(
                new MemoryTaskRepository(), new Clock(), new Ids(), new Gate(), new Diagnostics(),
                AttachmentRepository: attachments, DefaultAttachmentRoot: root));
            await module.ActivateAsync();

            var task = (await module.Api.CreateAsync(new("任务", ""))).Value;
            var add = await module.Attachments!.AddAsync(new(task.Id, source, task.Revision));
            Assert.True(add.IsSuccess, add.Error?.Message);
            task = add.Value;

            var storedCopies = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Assert.NotEmpty(storedCopies);

            var deleted = await module.Api.DeleteAsync(new(task.Id, task.Revision));
            Assert.True(deleted.IsSuccess, deleted.Error?.Message);

            foreach (var copy in storedCopies)
                Assert.False(File.Exists(copy), $"仓库内副本应随任务删除：{copy}");
            Assert.True(File.Exists(source), "用户源文件不应被删除");
            var remaining = await attachments.ListByTaskAsync(task.Id);
            Assert.True(remaining.IsSuccess);
            Assert.Empty(remaining.Value);
            File.Delete(source);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
