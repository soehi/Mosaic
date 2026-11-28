using System.Buffers.Text;
using System.Security.Cryptography;

namespace Mosaic.Server.Data;

public sealed class DatabaseMigrator(Database database, TimeProvider timeProvider)
{
    private static readonly (int Version, string Name, string Sql)[] Migrations =
    [
        (1, "initial", Schema.Migration1),
        (2, "session_management_and_sync_floor", Schema.Migration2),
        (4, "mobile_pairing", Schema.Migration4),
    ];

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                await connection.ExecuteAsync(
                    transaction,
                    """
                    CREATE TABLE IF NOT EXISTS schema_migrations(
                        version INTEGER PRIMARY KEY,
                        name TEXT NOT NULL,
                        applied_at INTEGER NOT NULL);
                    """,
                    token).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);

        var applied = await database.ReadAsync(
            async (connection, token) =>
            {
                var rows = await connection.QueryAsync(
                    transaction: null,
                    "SELECT version FROM schema_migrations",
                    reader => reader.Long("version"),
                    token).ConfigureAwait(false);
                return rows.ToHashSet();
            },
            cancellationToken).ConfigureAwait(false);

        var highestKnown = Migrations.Max(migration => migration.Version);
        if (applied.Count > 0 && applied.Max() > highestKnown)
        {
            throw new InvalidOperationException(
                $"数据库结构版本 {applied.Max()} 高于当前服务端支持的版本 {highestKnown}，已停止启动以保护数据。");
        }

        foreach (var migration in Migrations)
        {
            if (applied.Contains(migration.Version))
            {
                continue;
            }

            await database.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await connection.ExecuteAsync(transaction, migration.Sql, token).ConfigureAwait(false);
                    await connection.ExecuteAsync(
                        transaction,
                        "INSERT INTO schema_migrations(version, name, applied_at) VALUES($version, $name, $at)",
                        token,
                        ("$version", migration.Version),
                        ("$name", migration.Name),
                        ("$at", timeProvider.GetUtcNow().ToUnixTimeSeconds())).ConfigureAwait(false);
                    return 0;
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}

public static class MasterKeyStore
{
    public static byte[] Resolve(ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MasterKey is { Length: 32 } provided)
        {
            return provided;
        }

        var keyPath = Path.Combine(options.DataDirectory, "master.key");
        var databasePath = options.DatabasePath ?? Path.Combine(options.DataDirectory, "mosaic-server.sqlite3");
        if (File.Exists(keyPath))
        {
            var encoded = File.ReadAllText(keyPath).Trim();
            var key = Decode(encoded);
            if (key.Length != 32)
            {
                throw new InvalidOperationException("master.key 不是 32 字节密钥，已停止启动以保护数据。");
            }

            return key;
        }

        if (File.Exists(databasePath) && new FileInfo(databasePath).Length > 0)
        {
            throw new InvalidOperationException(
                "现有数据库缺少 master.key。请恢复原密钥或设置 MOSAIC_MASTER_KEY，已停止启动以保护数据。");
        }

        var generated = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(options.DataDirectory);
        var stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using (stream)
        {
            using var writer = new StreamWriter(stream);
            writer.Write(Base64Url.EncodeToString(generated));
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return generated;
    }

    private static byte[] Decode(string encoded)
    {
        try
        {
            return Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return Base64Url.DecodeFromChars(encoded);
        }
    }
}
