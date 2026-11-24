using System.Globalization;
using Mosaic.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Sqlite.Internal;

internal sealed class SqliteTaskRepository(IDatabaseSession database) : ITaskRepository
{
    public async Task<OperationResult> InsertAsync(TaskItemDto task, CancellationToken cancellationToken = default) =>
        WriteResult(await database.ExecuteAsync(new("INSERT INTO tasks(id,title,notes,completed,created_at,updated_at,revision,completed_at,archived_at,restored_at) VALUES($id,$title,$notes,$completed,$created,$updated,$revision,$completedAt,$archivedAt,$restoredAt)", Values(task)), cancellationToken).ConfigureAwait(false));
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
        var where = Where(query, out var values);
        // Preview lists only render title and status; skipping the notes column keeps the
        // row materialization cost proportional to the rows actually shown.
        var columns = query.IncludeNotes ? "*" : "id,title,completed,created_at,updated_at,revision,completed_at,archived_at,restored_at";
        var paging = "";
        if (query.Limit > 0)
        {
            paging = " LIMIT $limit OFFSET $offset";
            values["$limit"] = query.Limit;
            values["$offset"] = Math.Max(0, query.Offset);
        }
        var rows = await database.QueryAsync(new($"SELECT {columns} FROM tasks WHERE {where} ORDER BY {orderBy}, id COLLATE BINARY ASC{paging}", values), cancellationToken).ConfigureAwait(false);
        return rows.IsSuccess
            ? OperationResult<IReadOnlyList<TaskItemDto>>.Success(Array.AsReadOnly(rows.Value.Select(row => Map(row, query.IncludeNotes)).ToArray()))
            : OperationResult<IReadOnlyList<TaskItemDto>>.Failure(rows.Error!);
    }
    public async Task<OperationResult<TaskCountsDto>> QueryCountsAsync(TaskQuery query, CancellationToken cancellationToken = default)
    {
        var where = Where(query, out var values);
        var rows = await database.QueryAsync(new(
            $"SELECT COUNT(*) AS total, COALESCE(SUM(CASE WHEN completed = 0 THEN 1 ELSE 0 END), 0) AS open FROM tasks WHERE {where}",
            values), cancellationToken).ConfigureAwait(false);
        return rows.IsSuccess
            ? OperationResult<TaskCountsDto>.Success(new((int)rows.Value[0].Int64("total"), (int)rows.Value[0].Int64("open")))
            : OperationResult<TaskCountsDto>.Failure(rows.Error!);
    }
    private static string Where(TaskQuery query, out Dictionary<string, object?> values)
    {
        var scope = query.ArchiveScope switch
        {
            TaskArchiveScope.Active => " AND archived_at IS NULL",
            TaskArchiveScope.Archived => " AND archived_at IS NOT NULL",
            _ => ""
        };
        values = new Dictionary<string, object?>
        {
            ["$filter"] = (int)query.Filter,
            ["$completed"] = query.Filter == TaskFilter.Completed ? 1 : 0
        };
        return $"($filter=0 OR completed=$completed){scope}";
    }
    public async Task<OperationResult<IReadOnlyList<TaskItemDto>>> QueryArchiveCandidatesAsync(
        DateTimeOffset cutoffUtc, int limit, CancellationToken cancellationToken = default)
    {
        var rows = await database.QueryAsync(new(
            """
            SELECT * FROM tasks WHERE archived_at IS NULL AND completed = 1 AND completed_at IS NOT NULL
            AND MAX(completed_at, COALESCE(restored_at, completed_at)) <= $cutoff
            ORDER BY MAX(completed_at, COALESCE(restored_at, completed_at)), id LIMIT $limit
            """, new Dictionary<string, object?> { ["$cutoff"] = Utc(cutoffUtc), ["$limit"] = limit }), cancellationToken).ConfigureAwait(false);
        return rows.IsSuccess
            ? OperationResult<IReadOnlyList<TaskItemDto>>.Success(Array.AsReadOnly(rows.Value.Select(Map).ToArray()))
            : OperationResult<IReadOnlyList<TaskItemDto>>.Failure(rows.Error!);
    }
    public async Task<OperationResult> ReplaceAsync(TaskItemDto task, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var values = Values(task); values.Add("$expected", expectedRevision);
        return WriteResult(await database.ExecuteAsync(new("UPDATE tasks SET title=$title,notes=$notes,completed=$completed,updated_at=$updated,revision=$revision,completed_at=$completedAt,archived_at=$archivedAt,restored_at=$restoredAt WHERE id=$id AND revision=$expected", values), cancellationToken).ConfigureAwait(false));
    }
    public async Task<OperationResult> DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default) =>
        WriteResult(await database.ExecuteAsync(new("DELETE FROM tasks WHERE id=$id AND revision=$expected", new Dictionary<string, object?> { ["$id"] = id.ToString("D"), ["$expected"] = expectedRevision }), cancellationToken).ConfigureAwait(false));
    public async Task<OperationResult<TaskCreateOnceResult>> InsertOnceAsync(
        string sourceId, string requestKey, string payloadFingerprint, TaskItemDto task, CancellationToken cancellationToken = default)
    {
        var result = await database.TransactionAsync(session =>
        {
            var key = new Dictionary<string, object?> { ["$source"] = sourceId, ["$key"] = requestKey };
            var receipts = session.Query(new(
                "SELECT r.task_id,r.payload_fingerprint,CASE WHEN t.id IS NULL THEN 0 ELSE 1 END AS task_exists FROM task_creation_receipts r LEFT JOIN tasks t ON t.id=r.task_id WHERE r.source_id=$source AND r.request_key=$key", key));
            if (receipts.Count > 0)
            {
                var row = receipts[0];
                return row.String("payload_fingerprint") == payloadFingerprint
                    ? OperationResult<TaskCreateOnceResult>.Success(new(Guid.ParseExact(row.String("task_id"), "D"), TaskCreateOnceDisposition.AlreadyCreated, row.Int64("task_exists") == 1))
                    : OperationResult<TaskCreateOnceResult>.Failure(new(ErrorCodes.Conflict, "此创建请求标识已用于不同的任务内容。", "RequestKey"));
            }
            session.Execute(new(
                "INSERT INTO tasks(id,title,notes,completed,created_at,updated_at,revision,completed_at,archived_at,restored_at) VALUES($id,$title,$notes,$completed,$created,$updated,$revision,$completedAt,$archivedAt,$restoredAt)", Values(task)));
            session.Execute(new(
                "INSERT INTO task_creation_receipts(source_id,request_key,task_id,payload_fingerprint,created_at) VALUES($source,$key,$id,$fingerprint,$created)",
                new Dictionary<string, object?>
                {
                    ["$source"] = sourceId, ["$key"] = requestKey, ["$id"] = task.Id.ToString("D"),
                    ["$fingerprint"] = payloadFingerprint, ["$created"] = task.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                }));
            return OperationResult<TaskCreateOnceResult>.Success(new(task.Id, TaskCreateOnceDisposition.Created, true));
        }, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : OperationResult<TaskCreateOnceResult>.Failure(result.Error!);
    }
    public async Task<OperationResult<IReadOnlyList<TaskCreationReceiptDto>>> QueryCreationReceiptsAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        var result = await database.QueryAsync(new(
            "SELECT r.source_id,r.request_key,r.task_id,r.payload_fingerprint,r.created_at,CASE WHEN t.id IS NULL THEN 0 ELSE 1 END AS task_exists FROM task_creation_receipts r LEFT JOIN tasks t ON t.id=r.task_id WHERE r.source_id=$source ORDER BY r.request_key COLLATE BINARY",
            new Dictionary<string, object?> { ["$source"] = sourceId }), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? OperationResult<IReadOnlyList<TaskCreationReceiptDto>>.Success(Array.AsReadOnly(result.Value.Select(row => new TaskCreationReceiptDto(
                row.String("source_id"), row.String("request_key"), Guid.ParseExact(row.String("task_id"), "D"), row.String("payload_fingerprint"),
                DateTimeOffset.ParseExact(row.String("created_at"), "O", CultureInfo.InvariantCulture), row.Int64("task_exists") == 1)).ToArray()))
            : OperationResult<IReadOnlyList<TaskCreationReceiptDto>>.Failure(result.Error!);
    }
    private static OperationResult WriteResult(OperationResult<int> result) => !result.IsSuccess ? OperationResult.Failure(result.Error!) : result.Value == 1 ? OperationResult.Success() : OperationResult.Failure(new(ErrorCodes.Conflict, "任务已更改或删除，请重新加载。"));
    internal static Dictionary<string, object?> Values(TaskItemDto t) => new()
    {
        ["$id"] = t.Id.ToString("D"),
        ["$title"] = t.Title,
        ["$notes"] = t.Notes,
        ["$completed"] = t.IsCompleted ? 1 : 0,
        ["$created"] = t.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["$updated"] = t.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["$revision"] = t.Revision,
        ["$completedAt"] = Utc(t.CompletedAtUtc),
        ["$archivedAt"] = Utc(t.ArchivedAtUtc),
        ["$restoredAt"] = Utc(t.RestoredAtUtc)
    };
    private static TaskItemDto Map(SqlRow row) => Map(row, includeNotes: true);
    private static TaskItemDto Map(SqlRow row, bool includeNotes) => new(Guid.ParseExact(row.String("id"), "D"), row.String("title"), includeNotes ? row.String("notes") : "", row.Int64("completed") == 1,
        DateTimeOffset.ParseExact(row.String("created_at"), "O", CultureInfo.InvariantCulture), DateTimeOffset.ParseExact(row.String("updated_at"), "O", CultureInfo.InvariantCulture), row.Int64("revision"))
    {
        CompletedAtUtc = ReadUtc(row.String("completed_at")),
        ArchivedAtUtc = ReadUtc(row.String("archived_at")),
        RestoredAtUtc = ReadUtc(row.String("restored_at"))
    };
    private static string? Utc(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset? ReadUtc(string? value) => value is null ? null : DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);
}
