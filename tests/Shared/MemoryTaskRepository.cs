using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.TestSupport;

internal sealed class MemoryTaskRepository : ITaskRepository
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, TaskItemDto> _items = [];
    private readonly Dictionary<(string SourceId, string RequestKey), TaskCreationReceiptDto> _receipts = [];
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
            var ordered = Ordering is not null
                ? Ordering(matches)
                : query.SortOrder switch
                {
                    TaskSortOrder.CreatedAsc => matches.OrderBy(t => t.CreatedAtUtc).ThenBy(t => t.Id.ToString("D"), StringComparer.Ordinal),
                    TaskSortOrder.TitleAsc => matches.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Id.ToString("D"), StringComparer.Ordinal),
                    TaskSortOrder.TitleDesc => matches.OrderByDescending(t => t.Title, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Id.ToString("D"), StringComparer.Ordinal),
                    _ => matches.OrderByDescending(t => t.CreatedAtUtc).ThenBy(t => t.Id.ToString("D"), StringComparer.Ordinal)
                };
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
    public Task<OperationResult<TaskCreateOnceResult>> InsertOnceAsync(
        string sourceId, string requestKey, string payloadFingerprint, TaskItemDto task, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (Failure is not null) return Task.FromResult(OperationResult<TaskCreateOnceResult>.Failure(Failure));
            if (_receipts.TryGetValue((sourceId, requestKey), out var receipt))
                return Task.FromResult(receipt.PayloadFingerprint == payloadFingerprint
                    ? OperationResult<TaskCreateOnceResult>.Success(new(receipt.TaskId, TaskCreateOnceDisposition.AlreadyCreated, _items.ContainsKey(receipt.TaskId)))
                    : OperationResult<TaskCreateOnceResult>.Failure(new(ErrorCodes.Conflict, "Request key already used for a different payload.", "RequestKey")));
            _items.Add(task.Id, task);
            _receipts.Add((sourceId, requestKey), new(sourceId, requestKey, task.Id, payloadFingerprint, task.CreatedAtUtc, true));
            return Task.FromResult(OperationResult<TaskCreateOnceResult>.Success(new(task.Id, TaskCreateOnceDisposition.Created, true)));
        }
    }
    public Task<OperationResult<IReadOnlyList<TaskCreationReceiptDto>>> QueryCreationReceiptsAsync(
        string sourceId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (Failure is not null) return Task.FromResult(OperationResult<IReadOnlyList<TaskCreationReceiptDto>>.Failure(Failure));
            return Task.FromResult(OperationResult<IReadOnlyList<TaskCreationReceiptDto>>.Success(Array.AsReadOnly(
                _receipts.Values.Where(receipt => receipt.SourceId == sourceId)
                    .OrderBy(receipt => receipt.RequestKey, StringComparer.Ordinal)
                    .Select(receipt => receipt with { TaskExists = _items.ContainsKey(receipt.TaskId) }).ToArray())));
        }
    }
}
