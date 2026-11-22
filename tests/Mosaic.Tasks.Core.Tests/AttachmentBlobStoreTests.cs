using System.Reflection;
using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;
using Mosaic.TestSupport;

namespace Mosaic.Tasks.Core.Tests;

public sealed class AttachmentBlobStoreTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Ids : IIdGenerator { public Guid NewId() => Guid.NewGuid(); }
    private sealed class Gate : IModuleExecutionGate, IDisposable
    {
        private bool _active;
        public void Activate() => _active = true;
        public IDisposable? TryEnter() => _active ? this : null;
        public Task StopAsync() { _active = false; return Task.CompletedTask; }
        public void Dispose() { }
    }
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string id, Exception ex) { } }
    private sealed class MemoryAttachments : ITaskAttachmentRepository
    {
        public readonly List<TaskAttachmentDto> Items = [];
        public readonly Dictionary<Guid, string> Paths = [];
        public Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListByTaskAsync(Guid taskId, CancellationToken ct = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<TaskAttachmentDto>>.Success(Items.Where(a => a.TaskId == taskId).ToArray()));
        public Task<OperationResult> InsertAsync(TaskAttachmentDto attachment, string storedPath, CancellationToken ct = default)
        {
            Items.Add(attachment);
            Paths[attachment.Id] = storedPath;
            return Task.FromResult(OperationResult.Success());
        }
        public Task<OperationResult<TaskAttachmentDto?>> FindAsync(Guid attachmentId, CancellationToken ct = default) =>
            Task.FromResult(OperationResult<TaskAttachmentDto?>.Success(Items.FirstOrDefault(a => a.Id == attachmentId)));
        public Task<OperationResult<string?>> FindStoredPathAsync(Guid attachmentId, CancellationToken ct = default) =>
            Task.FromResult(OperationResult<string?>.Success(Paths.TryGetValue(attachmentId, out var path) ? path : null));
        public Task<OperationResult> DeleteAsync(Guid attachmentId, CancellationToken ct = default)
        {
            Items.RemoveAll(a => a.Id == attachmentId);
            Paths.Remove(attachmentId);
            return Task.FromResult(OperationResult.Success());
        }
        public Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListExpiredArchivedAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<OperationResult<long>> CountByTaskAsync(Guid taskId, CancellationToken ct = default) =>
            Task.FromResult(OperationResult<long>.Success((long)Items.Count(a => a.TaskId == taskId)));
        public Task<OperationResult<IReadOnlyDictionary<Guid, long>>> CountsByTasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct = default)
        {
            IReadOnlyDictionary<Guid, long> counts = taskIds.Distinct().ToDictionary(id => id, id => (long)Items.Count(a => a.TaskId == id)).Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => pair.Value);
            return Task.FromResult(OperationResult<IReadOnlyDictionary<Guid, long>>.Success(counts));
        }
    }

    private static (TasksModule Module, MemoryAttachments Repository, string Root, string Source) NewModule()
    {
        var root = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(Path.GetTempPath(), $"attachment-{Guid.NewGuid():D}.txt");
        File.WriteAllText(source, "payload");
        var repository = new MemoryAttachments();
        var module = new TasksModule(new(new MemoryTaskRepository(), new Clock(), new Ids(), new Gate(), new Diagnostics(),
            AttachmentRepository: repository, DefaultAttachmentRoot: root));
        return (module, repository, root, source);
    }

    [Fact]
    public async Task Open_read_returns_bytes_and_never_exposes_the_stored_path()
    {
        var (module, repository, root, source) = NewModule();
        await module.ActivateAsync();
        try
        {
            var task = (await module.Api.CreateAsync(new("任务", ""))).Value;
            var added = await module.Attachments!.AddAsync(new(task.Id, source, task.Revision));
            Assert.True(added.IsSuccess, added.Error?.Message);
            var attachment = Assert.Single((await module.Attachments!.ListAsync(task.Id)).Value);
            Assert.True(repository.Paths.ContainsKey(attachment.Id));

            var opened = await module.Attachments!.OpenReadAsync(task.Id, attachment.Id);
            Assert.True(opened.IsSuccess, opened.Error?.Message);
            await using (opened.Value)
            using (var reader = new StreamReader(opened.Value))
                Assert.Equal("payload", await reader.ReadToEndAsync());

            var missing = await module.Attachments!.OpenReadAsync(task.Id, Guid.NewGuid());
            Assert.Equal(ErrorCodes.NotFound, missing.Error?.Code);
            Assert.Equal("Tasks.Attachments.Error.NotFound", missing.Error?.MessageKey);
        }
        finally
        {
            await module.StopAsync();
            File.Delete(source);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Blob_store_ignores_paths_outside_the_managed_root()
    {
        var (module, _, root, _) = NewModule();
        await module.ActivateAsync();
        try
        {
            var storeType = typeof(TasksModule).Assembly
                .GetType("Mosaic.Tasks.Core.Internal.FileSystemTaskAttachmentBlobStore", throwOnError: true)!;
            var store = (ITaskAttachmentBlobStore)Activator.CreateInstance(storeType)!;
            storeType.GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store,
                [new Dictionary<string, object?> { ["tasks.attachments.storagePath"] = root }]);
            var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():D}.txt");
            await File.WriteAllTextAsync(outside, "keep");

            var deleted = await store.DeleteAsync(Guid.NewGuid(), Guid.NewGuid(), Path.GetFileName(outside), outside);
            Assert.True(deleted.IsSuccess);
            Assert.True(File.Exists(outside), "根目录外的文件不应被附件端口删除");
            Assert.False(store.IsWithinStore(outside));
            Assert.True(store.IsWithinStore(root));

            File.Delete(outside);
        }
        finally
        {
            await module.StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Without_root_new_attachments_are_refused_with_capability_error()
    {
        var source = Path.Combine(Path.GetTempPath(), $"attachment-{Guid.NewGuid():D}.txt");
        await File.WriteAllTextAsync(source, "payload");
        var module = new TasksModule(new(new MemoryTaskRepository(), new Clock(), new Ids(), new Gate(), new Diagnostics(),
            AttachmentRepository: new MemoryAttachments()));
        await module.ActivateAsync();
        try
        {
            var task = (await module.Api.CreateAsync(new("任务", ""))).Value;
            var added = await module.Attachments!.AddAsync(new(task.Id, source, task.Revision));
            Assert.Equal(ErrorCodes.CapabilityUnavailable, added.Error?.Code);
        }
        finally
        {
            await module.StopAsync();
            File.Delete(source);
        }
    }

    [Fact]
    public async Task Export_copies_out_and_rejects_targets_inside_the_store_or_existing_files()
    {
        var (module, _, root, source) = NewModule();
        await module.ActivateAsync();
        try
        {
            var task = (await module.Api.CreateAsync(new("任务", ""))).Value;
            task = (await module.Attachments!.AddAsync(new(task.Id, source, task.Revision))).Value;
            var attachment = Assert.Single((await module.Attachments!.ListAsync(task.Id)).Value);

            var hostDirectory = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(hostDirectory);
            try
            {
                var exported = await module.Attachments!.ExportAsync(new(task.Id, attachment.Id, hostDirectory, TaskAttachmentTransferMode.Copy, task.Revision));
                Assert.True(exported.IsSuccess, exported.Error?.Message);
                Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(hostDirectory, attachment.FileName)));

                var again = await module.Attachments!.ExportAsync(new(task.Id, attachment.Id, hostDirectory, TaskAttachmentTransferMode.Copy, exported.Value.Revision));
                Assert.Equal("Tasks.Attachments.Error.TargetExists", again.Error?.MessageKey);

                var inside = await module.Attachments!.ExportAsync(new(task.Id, attachment.Id, root, TaskAttachmentTransferMode.Copy, exported.Value.Revision));
                Assert.Equal(ErrorCodes.ValidationFailed, inside.Error?.Code);
            }
            finally { Directory.Delete(hostDirectory, recursive: true); }
        }
        finally
        {
            await module.StopAsync();
            File.Delete(source);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Re_adding_the_stored_file_is_refused_without_deleting_it()
    {
        var (module, repository, root, source) = NewModule();
        await module.ActivateAsync();
        try
        {
            var task = (await module.Api.CreateAsync(new("任务", ""))).Value;
            task = (await module.Attachments!.AddAsync(new(task.Id, source, task.Revision))).Value;
            var attachment = Assert.Single((await module.Attachments!.ListAsync(task.Id)).Value);
            var storedPath = repository.Paths[attachment.Id];
            Assert.True(File.Exists(storedPath));

            // 把附件根目录内的文件当作源再添加：必须被拒绝，且原文件保持完好。
            var again = await module.Attachments!.AddAsync(new(task.Id, storedPath, task.Revision));
            Assert.Equal("Tasks.Attachments.Error.AlreadyStored", again.Error?.MessageKey);
            Assert.True(File.Exists(storedPath), "拒绝重复添加时不应删除已存在的附件");
            Assert.Single((await module.Attachments!.ListAsync(task.Id)).Value);
        }
        finally
        {
            await module.StopAsync();
            File.Delete(source);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Existing_target_file_is_reported_as_conflict_and_preserved()
    {
        var (module, repository, root, source) = NewModule();
        await module.ActivateAsync();
        try
        {
            var task = (await module.Api.CreateAsync(new("任务", ""))).Value;
            task = (await module.Attachments!.AddAsync(new(task.Id, source, task.Revision))).Value;
            var attachment = Assert.Single((await module.Attachments!.ListAsync(task.Id)).Value);
            var storedPath = repository.Paths[attachment.Id];

            // 模拟同一布局位置已有内容（例如上次落库失败残留）：保存必须报冲突且不覆盖。
            repository.Items.Clear();
            await File.WriteAllTextAsync(storedPath, "original");
            var storeType = typeof(TasksModule).Assembly
                .GetType("Mosaic.Tasks.Core.Internal.FileSystemTaskAttachmentBlobStore", throwOnError: true)!;
            var store = (ITaskAttachmentBlobStore)Activator.CreateInstance(storeType)!;
            storeType.GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(store, [new Dictionary<string, object?> { ["tasks.attachments.storagePath"] = root }]);

            var saved = await store.SaveAsync(task.Id, attachment.Id, attachment.FileName, source);
            Assert.Equal("Tasks.Attachments.Error.SaveConflict", saved.Error?.MessageKey);
            Assert.Equal("original", await File.ReadAllTextAsync(storedPath));
        }
        finally
        {
            await module.StopAsync();
            File.Delete(source);
            Directory.Delete(root, recursive: true);
        }
    }
}