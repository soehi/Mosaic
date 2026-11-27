using System.Globalization;
using Microsoft.Data.Sqlite;
using Mosaic.Server.Data;

namespace Mosaic.Server.Tests;

public sealed class LoggingAndMigrationTests
{
    [Fact]
    public async Task Logs_never_contain_passwords_codes_or_tokens()
    {
        await using var host = await TestServerHost.StartAsync();
        var (email, password, session) = await host.RegisterAsync();
        var access = TestServerHost.AccessToken(session);
        var refresh = TestServerHost.RefreshToken(session);
        var registerCode = host.Email.LastCodeFor(email)!;

        var start = await TestServerHost.JsonAsync(await host.PostAsync("/v1/device/start", new { }));
        var userCode = start.GetProperty("userCode").GetString()!;
        var deviceCode = start.GetProperty("deviceCode").GetString()!;
        await host.PostAsync("/v1/device/approve", new { userCode }, access);
        await host.PostAsync("/v1/device/poll", new { deviceCode });
        await host.PostAsync("/v1/auth/login", new { email, password = "definitely-wrong-password" });
        await host.PostAsync("/v1/auth/refresh", new { refreshToken = refresh });

        var logs = string.Join('\n', host.Logs.Messages);
        Assert.NotEmpty(logs);
        Assert.Contains("auth.register", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(password, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(registerCode, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(access, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(refresh, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(userCode, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(deviceCode, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(email, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrator_is_idempotent_and_records_each_version_once()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mosaic-server-migrations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = new Database(Path.Combine(directory, "test.sqlite3"));
            var migrator = new DatabaseMigrator(database, TimeProvider.System);
            await migrator.ApplyAsync();
            await migrator.ApplyAsync();

            await using var connection = new SqliteConnection("Data Source=" + database.FilePath);
            await connection.OpenAsync();
            await using var versions = connection.CreateCommand();
            versions.CommandText = "SELECT COUNT(*) FROM schema_migrations";
            Assert.Equal(3L, Convert.ToInt64(await versions.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

            await using var floor = connection.CreateCommand();
            floor.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sync_floor'";
            Assert.Equal(1L, Convert.ToInt64(await floor.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

            await using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'users'";
            Assert.Equal(1L, Convert.ToInt64(await tables.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Master_key_file_is_created_once_and_reused()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mosaic-server-keys", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new ServerOptions
            {
                DataDirectory = directory,
                DatabasePath = Path.Combine(directory, "mosaic-server.sqlite3"),
            };
            var first = MasterKeyStore.Resolve(options);
            Assert.Equal(32, first.Length);
            var second = MasterKeyStore.Resolve(options);
            Assert.Equal(first, second);

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(Path.Combine(directory, "master.key"));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Existing_database_without_master_key_refuses_to_start()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mosaic-server-keys", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "mosaic-server.sqlite3");
            File.WriteAllText(databasePath, "not empty");
            var options = new ServerOptions
            {
                DataDirectory = directory,
                DatabasePath = databasePath,
            };

            var exception = Assert.Throws<InvalidOperationException>(() => MasterKeyStore.Resolve(options));
            Assert.Contains("master.key", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Data_and_sessions_survive_mobile_pairing_schema_upgrade(int previousVersion)
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var directory = Path.Combine(Path.GetTempPath(), "mosaic-server-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string email;
            string access;
            await using (var first = await TestServerHost.StartAsync(
                             options => options.MasterKey = key,
                             directory))
            {
                var (registered, _, session) = await first.RegisterAsync();
                email = registered;
                access = TestServerHost.AccessToken(session);
            }

            var path = Path.Combine(directory, "mosaic-server.sqlite3");
            await using var connection = new SqliteConnection("Data Source=" + path);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE mobile_pairings; DELETE FROM schema_migrations WHERE version = 4;";
            await command.ExecuteNonQueryAsync();
            if (previousVersion == 3)
            {
                command.CommandText = """
                    CREATE TABLE attachments(
                        id TEXT PRIMARY KEY,
                        user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                        task_id TEXT NOT NULL, file_name TEXT NOT NULL, size_bytes INTEGER NOT NULL,
                        sha256 TEXT NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
                    CREATE INDEX ix_attachments_user ON attachments(user_id, task_id);
                    INSERT INTO attachments SELECT 'attachment', id, 'task', 'existing.txt', 4, 'hash', 0, 0 FROM users;
                    INSERT INTO schema_migrations VALUES (3, 'attachments', 0);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using var second = await TestServerHost.StartAsync(
                options => options.MasterKey = key,
                directory);
            command.CommandText = "SELECT name FROM schema_migrations WHERE version = 4";
            Assert.Equal("mobile_pairing", await command.ExecuteScalarAsync());
            if (previousVersion == 3)
            {
                command.CommandText = "SELECT file_name FROM attachments WHERE id = 'attachment'";
                Assert.Equal("existing.txt", await command.ExecuteScalarAsync());
            }
            var login = await second.PostAsync("/v1/auth/login", new
            {
                email,
                password = TestServerHost.DefaultPassword,
            });
            Assert.Equal(System.Net.HttpStatusCode.OK, login.StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.OK, (await second.GetAsync("/v1/auth/me", access)).StatusCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
