using Microsoft.Data.Sqlite;
using Mosaic.Storage.Contracts;

namespace Mosaic.Storage.Sqlite.Internal;

internal sealed class SqliteSession(SqliteConnection connection, SqliteTransaction? transaction) : ITransactionSession, IDisposable
{
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private bool _disposed;
    private SqliteCommand Command(SqlStatement statement)
    {
        if (_disposed || Environment.CurrentManagedThreadId != _owner) throw new InvalidOperationException("Session is outside its callback lifetime.");
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = statement.Text;
        foreach (var (name, value) in statement.Parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public int Execute(SqlStatement statement) { using var c = Command(statement); return c.ExecuteNonQuery(); }
    public IReadOnlyList<SqlRow> Query(SqlStatement statement)
    {
        using var command = Command(statement);
        using var reader = command.ExecuteReader();
        var rows = new List<SqlRow>();
        while (reader.Read())
        {
            var names = new string[reader.FieldCount];
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                if (value is byte[]) throw new InvalidOperationException("Binary SQL values are not supported by the scalar protocol.");
                names[i] = reader.GetName(i);
                values[i] = value;
            }
            rows.Add(new SqlRow(names, values));
        }
        return rows.AsReadOnly();
    }
    public void Dispose() => _disposed = true;
}
