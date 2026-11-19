using System.Text.Json;
using Mosaic.Server.Data;
using Mosaic.Server.Security;

namespace Mosaic.Server.Emailing;

/// <summary>
/// Effective SMTP configuration: a row saved through the admin API overrides the
/// environment bootstrap values. The password is stored AES-GCM encrypted with a
/// key derived from the server master key and is never returned by read APIs.
/// </summary>
public sealed class EmailSettingsStore(
    Database database,
    ServerOptions options,
    SecretProtector protector,
    TimeProvider timeProvider)
{
    private const string RowKey = "smtp";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EmailSettings> GetAsync(CancellationToken cancellationToken)
    {
        var row = await ReadRowAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return options.Email;
        }

        var password = string.Empty;
        if (!string.IsNullOrEmpty(row.PasswordCipher))
        {
            try
            {
                password = protector.Decrypt(row.PasswordCipher);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                password = string.Empty;
            }
        }

        return new EmailSettings
        {
            Host = row.Host,
            Port = row.Port,
            Security = EmailSettings.ParseSecurity(row.Security),
            Username = row.Username,
            Password = password,
            From = row.From,
            FromName = row.FromName,
        };
    }

    public async Task<EmailSettingsView> GetViewAsync(CancellationToken cancellationToken)
    {
        var settings = await GetAsync(cancellationToken).ConfigureAwait(false);
        return new EmailSettingsView(
            settings.Host,
            settings.Port,
            EmailSettings.SerializeSecurity(settings.Security),
            settings.Username,
            settings.Password.Length > 0,
            settings.From,
            settings.FromName,
            settings.IsConfigured);
    }

    public async Task SaveAsync(EmailSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var row = new StoredSettings
        {
            Host = settings.Host,
            Port = settings.Port,
            Security = EmailSettings.SerializeSecurity(settings.Security),
            Username = settings.Username,
            From = settings.From,
            FromName = settings.FromName,
            PasswordCipher = settings.Password.Length == 0 ? string.Empty : protector.Encrypt(settings.Password),
        };
        var json = JsonSerializer.Serialize(row, JsonOptions);
        await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                await connection.ExecuteAsync(
                    transaction,
                    """
                    INSERT INTO server_settings(key, value, updated_at) VALUES('smtp', $value, $at)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
                    """,
                    token,
                    ("$value", json),
                    ("$at", timeProvider.GetUtcNow().ToUnixTimeSeconds())).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredSettings?> ReadRowAsync(CancellationToken cancellationToken) =>
        await database.ReadAsync(
            async (connection, token) =>
            {
                var value = (string?)await connection.ScalarAsync(
                    transaction: null,
                    "SELECT value FROM server_settings WHERE key = $key",
                    token,
                    ("$key", RowKey)).ConfigureAwait(false);
                return value is null ? null : JsonSerializer.Deserialize<StoredSettings>(value, JsonOptions);
            },
            cancellationToken).ConfigureAwait(false);

    private sealed record StoredSettings
    {
        public string Host { get; init; } = string.Empty;

        public int Port { get; init; } = 587;

        public string Security { get; init; } = "starttls";

        public string Username { get; init; } = string.Empty;

        public string From { get; init; } = string.Empty;

        public string FromName { get; init; } = "Mosaic";

        public string PasswordCipher { get; init; } = string.Empty;
    }
}
