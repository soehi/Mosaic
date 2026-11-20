using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Mosaic.Contracts;
using Mosaic.Storage.Contracts;

namespace Mosaic.Storage.Sqlite.Internal;

internal sealed class DatabaseWorker : IDatabaseSession, IInstallationState, IAsyncDisposable
{
    private readonly SqliteOptions _options;
    private readonly IDiagnosticSink _diagnostics;
    private readonly Channel<Action<SqliteConnection>> _queue;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _runner;
    private int _threadId;
    private int _stopping;

    internal DatabaseWorker(SqliteOptions options, IDiagnosticSink diagnostics)
    {
        if (!Path.IsPathFullyQualified(options.DatabasePath) || options.QueueCapacity < 1 || options.BusyTimeoutSeconds < 1)
            throw new ArgumentException("Invalid storage options.");
        _options = options;
        _diagnostics = diagnostics;
        _queue = Channel.CreateBounded<Action<SqliteConnection>>(new BoundedChannelOptions(options.QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _runner = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal Task StartAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    private void Run()
    {
        _threadId = Environment.CurrentManagedThreadId;
        try
        {
            var databaseExisted = File.Exists(_options.DatabasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)!);
            using var connection = OpenConnection();
            using (var transaction = connection.BeginTransaction())
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS mosaic_startup_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    INSERT OR IGNORE INTO mosaic_startup_metadata(key, value)
                    VALUES ('installation-classification-v1', $classification);
                    """;
                command.Parameters.AddWithValue("$classification", !_options.ConfigurationExisted && !databaseExisted ? "fresh" : "legacy");
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            _ready.TrySetResult();
            // Blocking waits are confined to this dedicated database thread, never the UI.
            if (_options.ConnectionMode == SqliteConnectionMode.PooledPerOperation)
            {
                while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    while (_queue.Reader.TryRead(out var work))
                    {
                        // Each operation gets an independent connection with a fresh transaction boundary;
                        // the single consumer queue still serializes every operation.
                        using var pooled = OpenConnection();
                        work(pooled);
                    }
                }
            }
            else
            {
                while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                    while (_queue.Reader.TryRead(out var work)) work(connection);
            }
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            _queue.Writer.TryComplete(ex);
            throw;
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = _options.BusyTimeoutSeconds
        }.ToString());
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = _options.JournalMode == SqliteJournalMode.Wal
                ? "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;"
                : "PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
        }
        return connection;
    }

    private async Task<OperationResult<T>> Enqueue<T>(Func<SqliteConnection, T> action, CancellationToken cancellationToken)
    {
        if (Environment.CurrentManagedThreadId == _threadId)
            throw new InvalidOperationException("Nested database queue access is forbidden.");
        if (Volatile.Read(ref _stopping) != 0)
            return OperationResult<T>.Failure(new(ErrorCodes.CapabilityUnavailable, "存储正在关闭。"));
        var result = new TaskCompletionSource<OperationResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _queue.Writer.WriteAsync(connection =>
            {
                // Cancellation is honored up to execution. Once a write starts, return the actual commit result.
                if (cancellationToken.IsCancellationRequested)
                {
                    result.SetResult(OperationResult<T>.Failure(new(ErrorCodes.Canceled, "操作已取消。")));
                    return;
                }
                try { result.SetResult(OperationResult<T>.Success(action(connection))); }
                catch (Exception ex)
                {
                    var id = Guid.NewGuid().ToString("N");
                    try { _diagnostics.Report(id, ex); }
                    catch { /* A diagnostics failure must not turn a handled storage error into a dead queue. */ }
                    result.TrySetResult(OperationResult<T>.Failure(new(ErrorCodes.StorageUnavailable, "本机存储暂不可用，请重试。", CorrelationId: id)));
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return OperationResult<T>.Failure(new(ErrorCodes.Canceled, "操作已取消。")); }
        catch (ChannelClosedException) { return OperationResult<T>.Failure(new(ErrorCodes.CapabilityUnavailable, "存储不可用。")); }
        return await result.Task.ConfigureAwait(false);
    }

    public Task<OperationResult<InstallationKind>> GetInstallationKindAsync(CancellationToken cancellationToken = default) =>
        Enqueue(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM mosaic_startup_metadata WHERE key='installation-classification-v1'";
            return (command.ExecuteScalar() as string) switch
            {
                "fresh" => InstallationKind.Fresh,
                "legacy" => InstallationKind.Legacy,
                _ => throw new InvalidOperationException("Startup installation classification is unavailable.")
            };
        }, cancellationToken);

    public Task<OperationResult<int>> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken = default) =>
        Enqueue(c => { using var s = new SqliteSession(c, null); return s.Execute(statement); }, cancellationToken);

    public Task<OperationResult<IReadOnlyList<SqlRow>>> QueryAsync(SqlStatement statement, CancellationToken cancellationToken = default) =>
        Enqueue(c => { using var s = new SqliteSession(c, null); return s.Query(statement); }, cancellationToken);

    public Task<OperationResult<T>> TransactionAsync<T>(Func<ITransactionSession, T> action, CancellationToken cancellationToken = default)
    {
        if (typeof(Task).IsAssignableFrom(typeof(T)) || typeof(T) == typeof(ValueTask) ||
            (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(ValueTask<>)))
            return Task.FromResult(OperationResult<T>.Failure(new(ErrorCodes.InvalidRequest, "事务回调必须同步完成。")));
        return Enqueue(c =>
        {
            using var transaction = c.BeginTransaction();
            using var session = new SqliteSession(c, transaction);
            var value = action(session);
            if (value is Task || value is ValueTask || (value is not null && value.GetType().IsGenericType && value.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>)))
                throw new InvalidOperationException("Transaction callbacks must be synchronous.");
            transaction.Commit();
            return value;
        }, cancellationToken);
    }

    public async Task<OperationResult> MigrateAsync(string moduleId, IReadOnlyList<MigrationDefinition> migrations, CancellationToken cancellationToken = default)
    {
        var snapshot = migrations.ToArray();
        var result = await Enqueue(c => { MigrationRunner.Apply(c, _options.DatabasePath, moduleId, snapshot, _options.BackupRetention); return true; }, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(result.Error!);
    }

    internal Task StopAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _queue.Writer.TryComplete();
        return _runner;
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
