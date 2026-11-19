using Mosaic.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Sqlite.Internal;

internal sealed class SqliteTaskTransferRepository(IDatabaseSession database) : ITaskTransferRepository
{
    private const string Insert =
        "INSERT INTO tasks(id,title,notes,completed,created_at,updated_at,revision,completed_at,archived_at,restored_at) VALUES($id,$title,$notes,$completed,$created,$updated,$revision,$completedAt,$archivedAt,$restoredAt)";

    // CREATE-records are kept: their task_id may now point at a restored row, which is exactly
    // what an idempotent create receipt should report. The table swap itself is atomic.
    public async Task<OperationResult> ReplaceAllAsync(IReadOnlyList<TaskItemDto> tasks, CancellationToken cancellationToken = default)
    {
        var result = await database.TransactionAsync(session =>
        {
            session.Execute(new("DELETE FROM tasks"));
            foreach (var task in tasks) session.Execute(new(Insert, SqliteTaskRepository.Values(task)));
            return OperationResult.Success();
        }, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : OperationResult.Failure(result.Error!);
    }
}
