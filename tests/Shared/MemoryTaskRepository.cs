using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.TestSupport;

internal sealed class MemoryTaskRepository : ITaskRepository
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, TaskItemDto> _items = [];
    internal OperationError? Failure { get; set; }
    internal Func<IEnumerable<TaskItemDto>, IEnumerable<TaskItemDto>>? Ordering { get; set; }
    public Task<OperationResult> InsertAsync(TaskItemDto task, CancellationToken cancellationToken = default)
    {
        lock (_sync) { if (Failure is not null) return Task.FromResult(OperationResult.Failure(Failure)); _items.Add(task.Id, task); return Task.FromResult(OperationResult.Success()); }
    }
    public Task<OperationResult<TaskItemDto?>> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_sync) return Task.FromResult(Failure is not null ? OperationResult<TaskItemDto?>.Failure(Failure) : OperationResult<TaskItemDto?>.Success(_items.GetValueOrDefault(id)));
    }
    public Task<OperationResult<IReadOnlyList<TaskItemDto>>> QueryAsync(TaskQuery query, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (Failure is not null) return Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Failure(Failure));
            var matches = _items.Values.Where(t => query.Filter == TaskFilter.All || t.IsCompleted == (query.Filter == TaskFilter.Completed));
            var ordered = Ordering is null ? matches.OrderByDescending(t => t.CreatedAtUtc).ThenBy(t => t.Id.ToString("D"), StringComparer.Ordinal) : Ordering(matches);
            return Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success(Array.AsReadOnly(ordered.ToArray())));
        }
    }
    public Task<OperationResult> ReplaceAsync(TaskItemDto task, long expectedRevision, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (Failure is not null) return Task.FromResult(OperationResult.Failure(Failure));
            if (!_items.TryGetValue(task.Id, out var current) || current.Revision != expectedRevision) return Task.FromResult(OperationResult.Failure(new(ErrorCodes.Conflict, "Conflict")));
            _items[task.Id] = task;
            return Task.FromResult(OperationResult.Success());
        }
    }
    public Task<OperationResult> DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (Failure is not null) return Task.FromResult(OperationResult.Failure(Failure));
            if (!_items.TryGetValue(id, out var current) || current.Revision != expectedRevision) return Task.FromResult(OperationResult.Failure(new(ErrorCodes.Conflict, "Conflict")));
            _items.Remove(id);
            return Task.FromResult(OperationResult.Success());
        }
    }
}
