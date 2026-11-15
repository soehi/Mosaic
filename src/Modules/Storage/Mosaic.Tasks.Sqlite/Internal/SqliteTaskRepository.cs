using System.Globalization;
using Mosaic.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Sqlite.Internal;

internal sealed class SqliteTaskRepository(IDatabaseSession database) : ITaskRepository
{
    public async Task<OperationResult> InsertAsync(TaskItemDto task, CancellationToken cancellationToken = default) =>
        WriteResult(await database.ExecuteAsync(new("INSERT INTO tasks(id,title,notes,completed,created_at,updated_at,revision) VALUES($id,$title,$notes,$completed,$created,$updated,$revision)", Values(task)), cancellationToken).ConfigureAwait(false));
    public async Task<OperationResult<TaskItemDto?>> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rows = await database.QueryAsync(new("SELECT * FROM tasks WHERE id=$id", new Dictionary<string, object?> { ["$id"] = id.ToString("D") }), cancellationToken).ConfigureAwait(false);
        return rows.IsSuccess ? OperationResult<TaskItemDto?>.Success(rows.Value.Count == 0 ? null : Map(rows.Value[0])) : OperationResult<TaskItemDto?>.Failure(rows.Error!);
    }
    public async Task<OperationResult<IReadOnlyList<TaskItemDto>>> QueryAsync(TaskQuery query, CancellationToken cancellationToken = default)
    {
        var orderBy = query.SortOrder switch
        {
            TaskSortOrder.CreatedAsc => "created_at ASC",
            TaskSortOrder.TitleAsc => "title COLLATE NOCASE ASC",
            TaskSortOrder.TitleDesc => "title COLLATE NOCASE DESC",
            _ => "created_at DESC"
        };
        var rows = await database.QueryAsync(new($"SELECT * FROM tasks WHERE ($filter=0 OR completed=$completed) ORDER BY {orderBy}, id COLLATE BINARY ASC", new Dictionary<string, object?>
        {
            ["$filter"] = (int)query.Filter,
            ["$completed"] = query.Filter == TaskFilter.Completed ? 1 : 0
        }), cancellationToken).ConfigureAwait(false);
        return rows.IsSuccess ? OperationResult<IReadOnlyList<TaskItemDto>>.Success(Array.AsReadOnly(rows.Value.Select(Map).ToArray())) : OperationResult<IReadOnlyList<TaskItemDto>>.Failure(rows.Error!);
    }
    public async Task<OperationResult> ReplaceAsync(TaskItemDto task, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var values = Values(task); values.Add("$expected", expectedRevision);
        return WriteResult(await database.ExecuteAsync(new("UPDATE tasks SET title=$title,notes=$notes,completed=$completed,updated_at=$updated,revision=$revision WHERE id=$id AND revision=$expected", values), cancellationToken).ConfigureAwait(false));
    }
    public async Task<OperationResult> DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default) =>
        WriteResult(await database.ExecuteAsync(new("DELETE FROM tasks WHERE id=$id AND revision=$expected", new Dictionary<string, object?> { ["$id"] = id.ToString("D"), ["$expected"] = expectedRevision }), cancellationToken).ConfigureAwait(false));
    private static OperationResult WriteResult(OperationResult<int> result) => !result.IsSuccess ? OperationResult.Failure(result.Error!) : result.Value == 1 ? OperationResult.Success() : OperationResult.Failure(new(ErrorCodes.Conflict, "任务已更改或删除，请重新加载。"));
    private static Dictionary<string, object?> Values(TaskItemDto t) => new()
    {
        ["$id"] = t.Id.ToString("D"),
        ["$title"] = t.Title,
        ["$notes"] = t.Notes,
        ["$completed"] = t.IsCompleted ? 1 : 0,
        ["$created"] = t.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["$updated"] = t.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["$revision"] = t.Revision
    };
    private static TaskItemDto Map(SqlRow row) => new(Guid.ParseExact(row.String("id"), "D"), row.String("title"), row.String("notes"), row.Int64("completed") == 1,
        DateTimeOffset.ParseExact(row.String("created_at"), "O", CultureInfo.InvariantCulture), DateTimeOffset.ParseExact(row.String("updated_at"), "O", CultureInfo.InvariantCulture), row.Int64("revision"));
}
