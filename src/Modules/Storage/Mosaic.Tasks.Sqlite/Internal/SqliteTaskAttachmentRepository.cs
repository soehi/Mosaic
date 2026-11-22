using System.Globalization;
using Mosaic.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Sqlite.Internal;

internal sealed class SqliteTaskAttachmentRepository(IDatabaseSession database) : ITaskAttachmentRepository
{
    private const string Columns = "id,task_id,file_name,stored_path,size_bytes,added_at";

    public async Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListByTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var result = await database.QueryAsync(new(
            $"SELECT {Columns} FROM task_attachments WHERE task_id = $task ORDER BY added_at, id",
            new Dictionary<string, object?> { ["$task"] = taskId.ToString("D") }), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? OperationResult<IReadOnlyList<TaskAttachmentDto>>.Success(result.Value.Select(Map).ToArray())
            : OperationResult<IReadOnlyList<TaskAttachmentDto>>.Failure(result.Error!);
    }

    public async Task<OperationResult> InsertAsync(TaskAttachmentDto attachment, CancellationToken cancellationToken = default)
    {
        var result = await database.ExecuteAsync(new(
            "INSERT INTO task_attachments(id,task_id,file_name,stored_path,size_bytes,added_at) VALUES($id,$task,$name,$path,$size,$added)",
            new Dictionary<string, object?>
            {
                ["$id"] = attachment.Id.ToString("D"),
                ["$task"] = attachment.TaskId.ToString("D"),
                ["$name"] = attachment.FileName,
                ["$path"] = attachment.StoredPath,
                ["$size"] = attachment.SizeBytes,
                ["$added"] = Utc(attachment.AddedAtUtc)!
            }), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(result.Error!);
    }

    public async Task<OperationResult<TaskAttachmentDto?>> FindAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var result = await database.QueryAsync(new(
            $"SELECT {Columns} FROM task_attachments WHERE id = $id",
            new Dictionary<string, object?> { ["$id"] = attachmentId.ToString("D") }), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) return OperationResult<TaskAttachmentDto?>.Failure(result.Error!);
        var row = result.Value.FirstOrDefault();
        return OperationResult<TaskAttachmentDto?>.Success(row is null ? null : Map(row));
    }

    public async Task<OperationResult> DeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var result = await database.ExecuteAsync(new(
            "DELETE FROM task_attachments WHERE id = $id",
            new Dictionary<string, object?> { ["$id"] = attachmentId.ToString("D") }), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(result.Error!);
    }

    public async Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListOrphanedAsync(CancellationToken cancellationToken = default)
    {
        var result = await database.QueryAsync(new(
            $"SELECT {Columns} FROM task_attachments WHERE task_id NOT IN (SELECT id FROM tasks)"), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? OperationResult<IReadOnlyList<TaskAttachmentDto>>.Success(result.Value.Select(Map).ToArray())
            : OperationResult<IReadOnlyList<TaskAttachmentDto>>.Failure(result.Error!);
    }

    public async Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListExpiredArchivedAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
    {
        var result = await database.QueryAsync(new(
            $"SELECT {"ta." + Columns.Replace(",", ",ta.")} FROM task_attachments ta JOIN tasks t ON t.id = ta.task_id WHERE t.archived_at IS NOT NULL AND t.archived_at <= $cutoff ORDER BY ta.added_at, ta.id",
            new Dictionary<string, object?> { ["$cutoff"] = Utc(cutoffUtc) }), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? OperationResult<IReadOnlyList<TaskAttachmentDto>>.Success(result.Value.Select(Map).ToArray())
            : OperationResult<IReadOnlyList<TaskAttachmentDto>>.Failure(result.Error!);
    }

    public async Task<OperationResult<long>> CountByTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var result = await database.QueryAsync(new(
            "SELECT COUNT(*) AS count FROM task_attachments WHERE task_id = $task",
            new Dictionary<string, object?> { ["$task"] = taskId.ToString("D") }), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? OperationResult<long>.Success(result.Value.Count == 0 ? 0 : result.Value[0].Int64("count"))
            : OperationResult<long>.Failure(result.Error!);
    }

    private static TaskAttachmentDto Map(SqlRow row) => new(
        Guid.ParseExact(row.String("id"), "D"),
        Guid.ParseExact(row.String("task_id"), "D"),
        row.String("file_name"),
        row.Int64("size_bytes"),
        DateTimeOffset.ParseExact(row.String("added_at"), "O", CultureInfo.InvariantCulture))
    { StoredPath = row.String("stored_path") };

    private static string? Utc(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
