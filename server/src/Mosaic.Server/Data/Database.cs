using Microsoft.Data.Sqlite;

namespace Mosaic.Server.Data;

/// <summary>
/// Thin SQLite access layer. Every mutation runs inside BEGIN IMMEDIATE so that
/// conditional updates (one-time codes, refresh rotation, device approval) are
/// serialized across concurrent requests.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string FilePath { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA busy_timeout=15000; PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public async Task<T> ReadAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await work(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> WriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        for (var attempt = 0; ; attempt++)
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var result = await work(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (SqliteException exception) when (IsBusy(exception) && attempt < 4)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                await Task.Delay(50 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static bool IsBusy(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6
        || exception.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase);

    private static async Task SafeRollbackAsync(SqliteTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The transaction already completed; nothing to roll back.
        }
        catch (SqliteException)
        {
            // The connection may already be gone; the server-side rollback still applies.
        }
    }
}
