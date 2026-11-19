using Microsoft.Data.Sqlite;

namespace Mosaic.Server.Data;

internal static class SqliteExtensions
{
    public static SqliteCommand Bind(
        this SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public static async Task<int> ExecuteAsync(
        this SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.Bind(transaction, commandText, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<object?> ScalarAsync(
        this SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.Bind(transaction, commandText, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DBNull ? null : value;
    }

    public static async Task<long> ScalarInt64Async(
        this SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        var value = await connection.ScalarAsync(transaction, commandText, cancellationToken, parameters)
            .ConfigureAwait(false);
        return value is null ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<List<T>> QueryAsync<T>(
        this SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(map);
        await using var command = connection.Bind(transaction, commandText, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(map(reader));
        }

        return results;
    }

    public static async Task<T?> SingleAsync<T>(
        this SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
        where T : class
    {
        var rows = await connection.QueryAsync(transaction, commandText, map, cancellationToken, parameters)
            .ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public static string Str(this SqliteDataReader reader, string column) =>
        reader.GetString(reader.GetOrdinal(column));

    public static string? StrOrNull(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static long Long(this SqliteDataReader reader, string column) =>
        reader.GetInt64(reader.GetOrdinal(column));

    public static long? LongOrNull(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    public static byte[] Blob(this SqliteDataReader reader, string column) =>
        (byte[])reader.GetValue(reader.GetOrdinal(column));

    public static bool Flag(this SqliteDataReader reader, string column) =>
        reader.GetInt64(reader.GetOrdinal(column)) != 0;
}
