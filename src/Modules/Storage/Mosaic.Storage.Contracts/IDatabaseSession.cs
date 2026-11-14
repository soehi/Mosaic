using System.Collections.ObjectModel;
using Mosaic.Contracts;

namespace Mosaic.Storage.Contracts;

// Values are materialized BCL scalars. No driver, connection or lazy reader escapes.
public sealed class SqlStatement
{
    public SqlStatement(string text, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        Text = text;
        var copy = new Dictionary<string, object?>();
        foreach (var (key, value) in parameters ?? new Dictionary<string, object?>())
        {
            if (value is not null and not string and not long and not int and not double and not bool)
                throw new ArgumentException("SQL parameters must be immutable scalar values.");
            copy.Add(key, value);
        }
        Parameters = new ReadOnlyDictionary<string, object?>(copy);
    }
    public string Text { get; }
    public IReadOnlyDictionary<string, object?> Parameters { get; }
}

public sealed class SqlRow
{
    private readonly string[] _names;
    private readonly object?[] _values;
    private readonly IReadOnlyDictionary<string, int> _ordinals;

    public SqlRow(IReadOnlyDictionary<string, object?> values) : this(values.Keys.ToArray(), values.Values.ToArray()) { }

    public SqlRow(IReadOnlyList<string> names, IReadOnlyList<object?> values)
    {
        if (names.Count != values.Count) throw new ArgumentException("Column names and values must have the same count.");
        _names = names.ToArray();
        _values = values.ToArray();
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < _names.Length; index++) ordinals.TryAdd(_names[index], index);
        _ordinals = new ReadOnlyDictionary<string, int>(ordinals);
    }

    public int ColumnCount => _values.Length;
    public IReadOnlyList<string> Names => Array.AsReadOnly(_names);
    public object? Value(int ordinal) => _values[ordinal];
    public string String(string name) => (string)_values[_ordinals[name]]!;
    public long Int64(string name) => Convert.ToInt64(_values[_ordinals[name]], System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record MigrationDefinition(int Version, string Sql);

// Valid only during the synchronous callback, on the storage worker thread.
public interface ITransactionSession
{
    int Execute(SqlStatement statement);
    IReadOnlyList<SqlRow> Query(SqlStatement statement);
}

public interface IDatabaseSession
{
    Task<OperationResult<int>> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyList<SqlRow>>> QueryAsync(SqlStatement statement, CancellationToken cancellationToken = default);
    Task<OperationResult<T>> TransactionAsync<T>(Func<ITransactionSession, T> action, CancellationToken cancellationToken = default);
    Task<OperationResult> MigrateAsync(string moduleId, IReadOnlyList<MigrationDefinition> migrations, CancellationToken cancellationToken = default);
}
