using System.Buffers;
using System.Security.Cryptography;
using Mosaic.Server.Data;
using Mosaic.Server.Http;

namespace Mosaic.Server.Attachments;

public sealed record AttachmentItem(
    string Id,
    string TaskId,
    string FileName,
    long SizeBytes,
    string Sha256,
    long CreatedAt,
    long UpdatedAt);

public sealed record AttachmentManifest(IReadOnlyList<AttachmentItem> Items, long UsageBytes);

public sealed record AttachmentWriteResult(AttachmentItem Item, bool Duplicate);

public sealed record AttachmentFile(AttachmentItem Item, string Path);

/// <summary>
/// Server-side storage for task attachment blobs. Metadata (name, size, hash,
/// task binding) lives in the <c>attachments</c> table; bytes live on disk under
/// <see cref="AttachmentPaths"/>. Uploads stream to a temp file, are hashed while
/// streaming, and are moved into place only after the row commits, so a crash
/// leaves either "row without blob" (repaired by the next upload) or an
/// unreferenced temp file, never a corrupt half-written blob.
/// </summary>
public sealed class AttachmentService(Database database, ServerOptions options, TimeProvider timeProvider)
{
    private const int CopyBufferSize = 81920;

    public long MaxBytes => options.AttachmentMaxBytes;

    public long QuotaBytes => options.AttachmentQuotaBytes;

    public async Task<AttachmentManifest> ListAsync(string userId, CancellationToken cancellationToken) =>
        await database.ReadAsync(
            async (connection, token) =>
            {
                var items = await connection.QueryAsync(
                    transaction: null,
                    """
                    SELECT id, task_id, file_name, size_bytes, sha256, created_at, updated_at
                    FROM attachments WHERE user_id = $user ORDER BY created_at, id
                    """,
                    MapItem,
                    token,
                    ("$user", userId)).ConfigureAwait(false);
                var usage = await connection.ScalarInt64Async(
                    transaction: null,
                    "SELECT COALESCE(SUM(size_bytes), 0) FROM attachments WHERE user_id = $user",
                    token,
                    ("$user", userId)).ConfigureAwait(false);
                return new AttachmentManifest(items, usage);
            },
            cancellationToken).ConfigureAwait(false);

    public async Task<AttachmentWriteResult> SaveAsync(
        string userId,
        string attachmentId,
        string? taskId,
        string? fileName,
        Stream body,
        long? contentLength,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var id = NormalizeAttachmentId(attachmentId);
        var task = NormalizeTaskId(taskId);
        var name = ValidateFileName(fileName);
        var expectedHash = ValidateHash(expectedSha256);
        if (contentLength > MaxBytes)
        {
            throw new ApiException(413, "attachment.too_large", $"单个附件不能超过 {MaxBytes} 字节。");
        }

        var directory = AttachmentPaths.UserDirectory(options, userId);
        Directory.CreateDirectory(directory);
        var normalized = AttachmentPaths.NormalizeAttachmentId(attachmentId);
        var extension = AttachmentPaths.Extension(name);
        var finalPath = Path.Combine(directory, normalized + extension);
        var tempPath = Path.Combine(directory, ".tmp-" + normalized + "-" + Guid.NewGuid().ToString("N"));

        long size;
        string sha256;
        try
        {
            (size, sha256) = await CopyAndHashAsync(body, tempPath, MaxBytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        if (expectedHash is not null && !string.Equals(expectedHash, sha256, StringComparison.Ordinal))
        {
            TryDelete(tempPath);
            throw new ApiException(400, "attachment.hash_mismatch", "附件内容校验失败，请重新上传。");
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        SaveOutcome outcome;
        try
        {
            outcome = await database.WriteAsync(
                async (connection, transaction, token) =>
                {
                    var existing = await connection.SingleAsync(
                        transaction,
                        """
                        SELECT id, user_id, task_id, file_name, size_bytes, sha256, created_at
                        FROM attachments WHERE id = $id
                        """,
                        reader => new ExistingRow(
                            reader.Str("id"),
                            reader.Str("user_id"),
                            reader.Str("task_id"),
                            reader.Str("file_name"),
                            reader.Long("size_bytes"),
                            reader.Str("sha256"),
                            reader.Long("created_at")),
                        token,
                        ("$id", id)).ConfigureAwait(false);

                    if (existing is not null && !string.Equals(existing.UserId, userId, StringComparison.Ordinal))
                    {
                        throw new ApiException(409, "attachment.conflict", "附件标识已被占用，请重新生成。");
                    }

                    if (existing is not null
                        && string.Equals(existing.TaskId, task, StringComparison.Ordinal)
                        && string.Equals(existing.FileName, name, StringComparison.Ordinal)
                        && existing.SizeBytes == size
                        && string.Equals(existing.Sha256, sha256, StringComparison.Ordinal)
                        && File.Exists(finalPath))
                    {
                        // Same bytes already stored: the client is retrying an upload it
                        // may not have seen acknowledged. Never rewrite the blob.
                        return new SaveOutcome(
                            new AttachmentItem(
                                existing.Id,
                                existing.TaskId,
                                existing.FileName,
                                existing.SizeBytes,
                                existing.Sha256,
                                existing.CreatedAt,
                                existing.CreatedAt),
                            Duplicate: true,
                            PreviousPath: null);
                    }

                    var usage = await connection.ScalarInt64Async(
                        transaction,
                        """
                        SELECT COALESCE(SUM(size_bytes), 0) FROM attachments
                        WHERE user_id = $user AND id <> $id
                        """,
                        token,
                        ("$user", userId),
                        ("$id", id)).ConfigureAwait(false);
                    if (usage + size > QuotaBytes)
                    {
                        throw new ApiException(413, "attachment.quota_exceeded", "附件空间已满，请先删除部分附件。");
                    }

                    await connection.ExecuteAsync(
                        transaction,
                        """
                        INSERT INTO attachments(id, user_id, task_id, file_name, size_bytes, sha256, created_at, updated_at)
                        VALUES($id, $user, $task, $name, $size, $sha, $now, $now)
                        ON CONFLICT(id) DO UPDATE SET
                            task_id = excluded.task_id,
                            file_name = excluded.file_name,
                            size_bytes = excluded.size_bytes,
                            sha256 = excluded.sha256,
                            updated_at = excluded.updated_at
                        """,
                        token,
                        ("$id", id),
                        ("$user", userId),
                        ("$task", task),
                        ("$name", name),
                        ("$size", size),
                        ("$sha", sha256),
                        ("$now", now)).ConfigureAwait(false);

                    var previous = existing is null
                        ? null
                        : Path.Combine(directory, AttachmentPaths.NormalizeAttachmentId(existing.Id)
                            + AttachmentPaths.Extension(existing.FileName));
                    return new SaveOutcome(
                        new AttachmentItem(
                            id,
                            task,
                            name,
                            size,
                            sha256,
                            existing?.CreatedAt ?? now,
                            now),
                        Duplicate: false,
                        PreviousPath: previous);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            TryDelete(tempPath);
            throw new ApiException(409, "attachment.conflict", "附件写入冲突，请重试。");
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        if (outcome.Duplicate)
        {
            TryDelete(tempPath);
            return new AttachmentWriteResult(outcome.Item, true);
        }

        try
        {
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The row exists but the blob does not: the next identical upload repairs it.
            throw new ApiException(500, "attachment.storage_failed", "附件写入磁盘失败，请重试。");
        }

        if (outcome.PreviousPath is not null
            && !string.Equals(outcome.PreviousPath, finalPath, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(outcome.PreviousPath);
        }

        return new AttachmentWriteResult(outcome.Item, false);
    }

    public async Task<AttachmentFile?> OpenAsync(
        string userId,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        var id = NormalizeAttachmentId(attachmentId);
        var item = await database.ReadAsync(
            async (connection, token) =>
                await connection.SingleAsync(
                    transaction: null,
                    """
                    SELECT id, task_id, file_name, size_bytes, sha256, created_at, updated_at
                    FROM attachments WHERE id = $id AND user_id = $user
                    """,
                    MapItem,
                    token,
                    ("$id", id),
                    ("$user", userId)).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var path = AttachmentPaths.FileFor(options, userId, item.Id, item.FileName);
        return File.Exists(path) ? new AttachmentFile(item, path) : null;
    }

    public async Task<bool> DeleteAsync(
        string userId,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        var id = NormalizeAttachmentId(attachmentId);
        var removed = await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var path = await connection.SingleAsync(
                    transaction,
                    "SELECT file_name FROM attachments WHERE id = $id AND user_id = $user",
                    reader => reader.Str("file_name"),
                    token,
                    ("$id", id),
                    ("$user", userId)).ConfigureAwait(false);
                if (path is null)
                {
                    return null;
                }

                await connection.ExecuteAsync(
                    transaction,
                    "DELETE FROM attachments WHERE id = $id AND user_id = $user",
                    token,
                    ("$id", id),
                    ("$user", userId)).ConfigureAwait(false);
                return path;
            },
            cancellationToken).ConfigureAwait(false);
        if (removed is null)
        {
            return false;
        }

        TryDelete(AttachmentPaths.FileFor(options, userId, id, removed));
        return true;
    }

    private static async Task<(long Size, string Sha256)> CopyAndHashAsync(
        Stream body,
        string tempPath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        long size = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var target = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                size += read;
                if (size > maxBytes)
                {
                    throw new ApiException(413, "attachment.too_large", $"单个附件不能超过 {maxBytes} 字节。");
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static AttachmentItem MapItem(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(
            reader.Str("id"),
            reader.Str("task_id"),
            reader.Str("file_name"),
            reader.Long("size_bytes"),
            reader.Str("sha256"),
            reader.Long("created_at"),
            reader.Long("updated_at"));

    private static string NormalizeAttachmentId(string attachmentId)
    {
        if (!Guid.TryParse(attachmentId, out var parsed) || parsed == Guid.Empty)
        {
            throw new ApiException(400, "attachment.invalid", "附件标识必须是有效的 Guid。");
        }

        return parsed.ToString("D");
    }

    private static string NormalizeTaskId(string? taskId)
    {
        if (!Guid.TryParse(taskId, out var parsed) || parsed == Guid.Empty)
        {
            throw new ApiException(400, "attachment.invalid", "taskId 必须是有效的 Guid。");
        }

        return parsed.ToString("D");
    }

    private static string ValidateFileName(string? fileName)
    {
        var name = (fileName ?? string.Empty).Trim();
        if (name.Length == 0 || name is "." or "..")
        {
            throw new ApiException(400, "attachment.invalid", "fileName 无效。");
        }

        if (name.EnumerateRunes().Count() > AttachmentPaths.MaxFileNameCharacters)
        {
            throw new ApiException(400, "attachment.invalid", "fileName 过长。");
        }

        var invalid = Path.GetInvalidFileNameChars();
        foreach (var character in name)
        {
            if (char.IsControl(character) || Array.IndexOf(invalid, character) >= 0)
            {
                throw new ApiException(400, "attachment.invalid", "fileName 包含无效字符。");
            }
        }

        return name;
    }

    private static string? ValidateHash(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return null;
        }

        var value = sha256.Trim().ToLowerInvariant();
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new ApiException(400, "attachment.invalid", "X-Mosaic-Sha256 必须是 SHA-256 十六进制值。");
        }

        return value;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Wasted bytes only; the manifest never references an unreachable blob.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    private sealed record ExistingRow(
        string Id,
        string UserId,
        string TaskId,
        string FileName,
        long SizeBytes,
        string Sha256,
        long CreatedAt);

    private sealed record SaveOutcome(AttachmentItem Item, bool Duplicate, string? PreviousPath);
}