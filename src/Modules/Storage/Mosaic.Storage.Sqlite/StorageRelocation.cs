using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mosaic.Contracts;
using Mosaic.Storage.Contracts;

namespace Mosaic.Storage.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IStorageRelocation"/>. Planning runs inside the running app;
/// <see cref="ExecuteAsync"/> runs on startup before <see cref="SqliteModule"/> opens the database.
/// </summary>
public sealed class SqliteStorageRelocation : IStorageRelocation
{
    private const long RequiredHeadroomBytes = 16L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _databasePath;
    private readonly string _configDirectory;

    public SqliteStorageRelocation(string databasePath, string configDirectory)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _configDirectory = configDirectory;
    }

    public Task<OperationResult<StorageLocationInfo>> GetLocationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new StorageLocationInfo(
            Path.GetDirectoryName(_databasePath)!,
            _databasePath,
            File.Exists(_databasePath) ? new FileInfo(_databasePath).Length : 0);
        return Task.FromResult(OperationResult<StorageLocationInfo>.Success(info));
    }

    public Task<OperationResult<StorageRelocationPlan>> PlanAsync(
        StorageRelocationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.TargetDirectory))
            return Task.FromResult(Failure("请选择目标文件夹。", "target"));
        if (!Path.IsPathFullyQualified(request.TargetDirectory))
            return Task.FromResult(Failure("目标文件夹必须是绝对路径。", "target"));
        string target;
        try { target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.TargetDirectory)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(Failure("目标文件夹路径无效。", "target"));
        }
        if (string.Equals(target, Path.GetPathRoot(target)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Failure("不能把数据库迁移到磁盘根目录。", "target"));
        if (!Directory.Exists(target))
            return Task.FromResult(Failure("目标文件夹不存在，请先创建。", "target"));
        var sourceDirectory = Path.GetDirectoryName(_databasePath)!;
        if (string.Equals(target, sourceDirectory, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Failure("目标文件夹与当前数据库位置相同。", "target"));
        if (!File.Exists(_databasePath))
            return Task.FromResult(Failure("当前数据库不存在，无需迁移。", "target"));
        var filename = Path.GetFileName(_databasePath);
        var targetPath = Path.Combine(target, filename);
        if (File.Exists(targetPath))
            return Task.FromResult(Failure("目标文件夹已存在同名数据库，请换一个文件夹。", "target"));
        var sourceBytes = new FileInfo(_databasePath).Length;
        var available = AvailableFreeSpace(target);
        if (available >= 0 && available < sourceBytes + RequiredHeadroomBytes)
            return Task.FromResult(Failure("目标磁盘可用空间不足。", "target"));
        return Task.FromResult(OperationResult<StorageRelocationPlan>.Success(
            new StorageRelocationPlan(_databasePath, target, targetPath, sourceBytes, request.KeepSource)));

        static OperationResult<StorageRelocationPlan> Failure(string message, string field) =>
            OperationResult<StorageRelocationPlan>.Failure(new(ErrorCodes.InvalidRequest, message, field));
    }

    public async Task<OperationResult> ScheduleAsync(
        StorageRelocationPlan plan,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(_configDirectory);
            var path = Path.Combine(_configDirectory, StorageRelocationFiles.RequestFileName);
            var temporary = path + ".tmp";
            var payload = new PendingRequest(plan.TargetDirectory, plan.KeepSource);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return OperationResult.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return OperationResult.Failure(new(ErrorCodes.StorageUnavailable, "无法写入迁移请求：" + exception.Message));
        }
    }

    /// <summary>Reads a pending relocation request, tolerating a missing or unreadable file.</summary>
    public static async Task<StorageRelocationRequest?> ReadRequestAsync(
        string configDirectory,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(configDirectory, StorageRelocationFiles.RequestFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<PendingRequest>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.TargetDirectory)) return null;
            return new StorageRelocationRequest(payload.TargetDirectory, payload.KeepSource);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void DeleteRequest(string configDirectory)
    {
        try
        {
            var path = Path.Combine(configDirectory, StorageRelocationFiles.RequestFileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale request would retry the move next startup; the plan rejects an existing target.
        }
    }

    /// <summary>Writes the outcome of the last startup relocation for diagnostics and the maintenance page.</summary>
    public static void WriteResult(string configDirectory, bool success, string message, string? databasePath)
    {
        try
        {
            Directory.CreateDirectory(configDirectory);
            var path = Path.Combine(configDirectory, StorageRelocationFiles.ResultFileName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new RelocationResult(success, message, databasePath, DateTimeOffset.Now), JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diagnostics only; never block startup on the result file.
        }
    }

    public Task<StorageRelocationResult?> ReadResultAsync(CancellationToken cancellationToken = default) =>
        ReadResultCoreAsync(_configDirectory, cancellationToken);

    /// <summary>Reads and removes the last relocation outcome; missing or unreadable files yield null.</summary>
    private static async Task<StorageRelocationResult?> ReadResultCoreAsync(
        string configDirectory,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(configDirectory, StorageRelocationFiles.ResultFileName);
        if (!File.Exists(path)) return null;
        StorageRelocationResult? result = null;
        try
        {
            var payload = JsonSerializer.Deserialize<RelocationResult>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
            if (payload is not null)
                result = new StorageRelocationResult(
                    payload.Success, payload.Message, payload.DatabasePath, payload.CompletedAt);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt result file carries no information; discard it below.
        }
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>
    /// Copies the database with SQLite's online backup API, verifies the copy, then moves it into place.
    /// Must only be called when no other connection holds the source database.
    /// </summary>
    public static async Task<OperationResult> ExecuteAsync(
        StorageRelocationPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(plan.SourceDatabasePath))
            return OperationResult.Failure(new(ErrorCodes.NotFound, "源数据库不存在，迁移已取消。"));
        if (File.Exists(plan.TargetDatabasePath))
            return OperationResult.Failure(new(ErrorCodes.Conflict, "目标位置已存在数据库，迁移已取消。"));
        var temporary = plan.TargetDatabasePath + ".migrating";
        try
        {
            Directory.CreateDirectory(plan.TargetDirectory);
            TryDelete(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            var copied = await Task.Run(() => CopyVerified(plan.SourceDatabasePath, temporary, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (!copied.IsSuccess) { TryDelete(temporary); return copied; }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, plan.TargetDatabasePath);
            if (!plan.KeepSource) DeleteSourceFiles(plan.SourceDatabasePath);
            return OperationResult.Success();
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }
        catch (Exception exception)
        {
            TryDelete(temporary);
            return OperationResult.Failure(new(ErrorCodes.StorageUnavailable, "迁移失败，原数据保持不变：" + exception.Message));
        }
    }

    private static OperationResult CopyVerified(string sourcePath, string temporaryPath, CancellationToken cancellationToken)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        source.Open();
        using (var checkpoint = source.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        var integrity = IntegrityCheck(source);
        if (integrity is not null)
            return OperationResult.Failure(new(ErrorCodes.StorageUnavailable, "源数据库校验失败：" + integrity));
        cancellationToken.ThrowIfCancellationRequested();
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = temporaryPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            target.Open();
            source.BackupDatabase(target);
        }
        using (var verify = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = temporaryPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        {
            verify.Open();
            var result = IntegrityCheck(verify);
            if (result is not null)
                return OperationResult.Failure(new(ErrorCodes.StorageUnavailable, "迁移副本校验失败：" + result));
        }
        return OperationResult.Success();
    }

    private static string? IntegrityCheck(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var value = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase) ? null : value ?? "unknown";
    }

    private static void DeleteSourceFiles(string sourcePath)
    {
        foreach (var path in new[] { sourcePath, sourcePath + "-wal", sourcePath + "-shm" })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static long AvailableFreeSpace(string directory)
    {
        try
        {
            var current = directory;
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
                current = Path.GetDirectoryName(current);
            return string.IsNullOrEmpty(current) ? -1 : new DriveInfo(Path.GetPathRoot(current)!).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private sealed record PendingRequest(string TargetDirectory, bool KeepSource);

    private sealed record RelocationResult(bool Success, string Message, string? DatabasePath, DateTimeOffset CompletedAt);
}
