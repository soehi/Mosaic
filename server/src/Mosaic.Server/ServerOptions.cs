using System.Globalization;
using System.Net;
using Mosaic.Server.Emailing;

namespace Mosaic.Server;

public enum RegistrationMode
{
    Open,
    Closed,
}

/// <summary>
/// All deployment knobs. Secrets come from environment variables or Docker secrets;
/// nothing in this type is ever serialized back to a client.
/// </summary>
public sealed class ServerOptions
{
    public string DataDirectory { get; set; } = "data";

    public string? DatabasePath { get; set; }

    public string PublicUrl { get; set; } = "http://localhost:5080";

    public string ServerName { get; set; } = "Mosaic";

    public RegistrationMode RegistrationMode { get; set; } = RegistrationMode.Open;

    public byte[]? MasterKey { get; set; }

    public string? AdminToken { get; set; }

    public string[] TrustedProxies { get; set; } = [];

    public long BackupQuotaBytes { get; set; } = 50L * 1024 * 1024;

    public int BackupRetention { get; set; } = 20;

    public int MaxItemsPerBackup { get; set; } = 20_000;

    public long MaxRequestBodyBytes { get; set; } = 16L * 1024 * 1024;

    public long AccessTokenSeconds { get; set; } = 900;

    public long RefreshTokenSeconds { get; set; } = 14L * 24 * 60 * 60;

    public int EmailCodeTtlSeconds { get; set; } = 600;

    public int EmailCodeMaxAttempts { get; set; } = 5;

    public int EmailCodeResendSeconds { get; set; } = 60;

    public int EmailAddressLimitPerHour { get; set; } = 6;

    public int EmailIpLimitPerWindow { get; set; } = 12;

    public int EmailIpWindowSeconds { get; set; } = 600;

    public int LoginIpLimitPerWindow { get; set; } = 30;

    public int LoginIpWindowSeconds { get; set; } = 600;

    public int LoginFailureLimit { get; set; } = 10;

    public int LoginFailureWindowSeconds { get; set; } = 900;

    public long DeviceCodeTtlSeconds { get; set; } = 600;

    /// <summary>How long sync change history is kept before clients must fall back to a backup snapshot.</summary>
    public long SyncHistorySeconds { get; set; } = 90L * 24 * 60 * 60;

    /// <summary>Test seam: a deterministic clock.</summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>Test seam: replaces the SMTP transport while keeping configuration rules.</summary>
    public IEmailSender? EmailSenderOverride { get; set; }

    /// <summary>Test seam: an extra log provider used to assert that no secrets reach the log.</summary>
    public ILoggerProvider? LogProvider { get; set; }

    public EmailSettings Email { get; set; } = new();

    public static ServerOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new ServerOptions
        {
            DataDirectory = Text(configuration, "MOSAIC_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data"),
            DatabasePath = Text(configuration, "MOSAIC_DATABASE_PATH"),
            PublicUrl = Text(configuration, "MOSAIC_PUBLIC_URL") ?? "http://localhost:5080",
            ServerName = Text(configuration, "MOSAIC_SERVER_NAME") ?? "Mosaic",
            AdminToken = Text(configuration, "MOSAIC_ADMIN_TOKEN"),
            BackupQuotaBytes = Number(configuration, "MOSAIC_BACKUP_QUOTA_BYTES", 50L * 1024 * 1024),
            BackupRetention = (int)Number(configuration, "MOSAIC_BACKUP_RETENTION", 20),
            MaxRequestBodyBytes = Number(configuration, "MOSAIC_MAX_BODY_BYTES", 16L * 1024 * 1024),
            AccessTokenSeconds = Number(configuration, "MOSAIC_ACCESS_TOKEN_SECONDS", 900),
            RefreshTokenSeconds = Number(configuration, "MOSAIC_REFRESH_TOKEN_SECONDS", 14L * 24 * 60 * 60),
            EmailCodeTtlSeconds = (int)Number(configuration, "MOSAIC_EMAIL_CODE_TTL_SECONDS", 600),
            EmailCodeMaxAttempts = (int)Number(configuration, "MOSAIC_EMAIL_MAX_ATTEMPTS", 5),
            EmailCodeResendSeconds = (int)Number(configuration, "MOSAIC_EMAIL_RESEND_SECONDS", 60),
            EmailAddressLimitPerHour = (int)Number(configuration, "MOSAIC_EMAIL_ADDRESS_LIMIT_PER_HOUR", 6),
            EmailIpLimitPerWindow = (int)Number(configuration, "MOSAIC_EMAIL_IP_LIMIT", 12),
            EmailIpWindowSeconds = (int)Number(configuration, "MOSAIC_EMAIL_IP_WINDOW_SECONDS", 600),
            LoginIpLimitPerWindow = (int)Number(configuration, "MOSAIC_LOGIN_IP_LIMIT", 30),
            LoginIpWindowSeconds = (int)Number(configuration, "MOSAIC_LOGIN_IP_WINDOW_SECONDS", 600),
            LoginFailureLimit = (int)Number(configuration, "MOSAIC_LOGIN_FAILURE_LIMIT", 10),
            LoginFailureWindowSeconds = (int)Number(configuration, "MOSAIC_LOGIN_FAILURE_WINDOW_SECONDS", 900),
            DeviceCodeTtlSeconds = Number(configuration, "MOSAIC_DEVICE_TTL_SECONDS", 600),
            SyncHistorySeconds = Number(configuration, "MOSAIC_SYNC_HISTORY_DAYS", 90) * 24 * 60 * 60,
            TrustedProxies = (Text(configuration, "MOSAIC_TRUSTED_PROXIES") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Email = new EmailSettings
            {
                Host = Text(configuration, "MOSAIC_SMTP_HOST") ?? string.Empty,
                Port = (int)Number(configuration, "MOSAIC_SMTP_PORT", 587),
                Security = EmailSettings.ParseSecurity(Text(configuration, "MOSAIC_SMTP_SECURITY")),
                Username = Text(configuration, "MOSAIC_SMTP_USERNAME") ?? string.Empty,
                Password = Text(configuration, "MOSAIC_SMTP_PASSWORD") ?? string.Empty,
                From = Text(configuration, "MOSAIC_SMTP_FROM") ?? string.Empty,
                FromName = Text(configuration, "MOSAIC_SMTP_FROM_NAME") ?? "Mosaic",
            },
        };

        var mode = Text(configuration, "MOSAIC_REGISTRATION_MODE");
        if (!string.IsNullOrWhiteSpace(mode))
        {
            options.RegistrationMode = mode.Trim().ToLowerInvariant() switch
            {
                "open" => RegistrationMode.Open,
                "closed" => RegistrationMode.Closed,
                _ => throw new InvalidOperationException("MOSAIC_REGISTRATION_MODE 只能是 open 或 closed。"),
            };
        }

        options.Normalize();
        options.Validate();
        return options;
    }

    public void Normalize()
    {
        DataDirectory = Path.GetFullPath(DataDirectory, Directory.GetCurrentDirectory());
        DatabasePath ??= Path.Combine(DataDirectory, "mosaic-server.sqlite3");
        PublicUrl = PublicUrl.Trim().TrimEnd('/');
        if (Email.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("MOSAIC_SMTP_PORT 必须在 1 到 65535 之间。");
        }
    }

    public void Validate()
    {
        if (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var publicUri)
            || (publicUri.Scheme != Uri.UriSchemeHttp && publicUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("MOSAIC_PUBLIC_URL 必须是有效的 http/https 绝对地址。");
        }

        if (publicUri.Query.Length > 0 || publicUri.Fragment.Length > 0 || publicUri.UserInfo.Length > 0)
        {
            throw new InvalidOperationException("MOSAIC_PUBLIC_URL 不能包含查询参数、片段或用户信息。");
        }

        if (BackupQuotaBytes <= 0 || BackupRetention < 1 || MaxItemsPerBackup < 1)
        {
            throw new InvalidOperationException("备份配额、保留份数和单份条目上限必须为正数。");
        }

        if (AccessTokenSeconds <= 0 || RefreshTokenSeconds <= 0 || DeviceCodeTtlSeconds <= 0
            || EmailCodeTtlSeconds <= 0 || EmailCodeResendSeconds <= 0 || SyncHistorySeconds <= 0)
        {
            throw new InvalidOperationException("令牌与验证码有效期必须为正数。");
        }

        if (MaxRequestBodyBytes < 1024)
        {
            throw new InvalidOperationException("MOSAIC_MAX_BODY_BYTES 太小。");
        }

        foreach (var proxy in TrustedProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                throw new InvalidOperationException($"MOSAIC_TRUSTED_PROXIES 包含无效地址：{proxy}");
            }
        }

        if (MasterKey is { Length: > 0 } && MasterKey.Length != 32)
        {
            throw new InvalidOperationException("MOSAIC_MASTER_KEY 必须是 32 字节（base64 或 base64url）。");
        }

        PublicUrl = publicUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string? Text(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static long Number(IConfiguration configuration, string key, long fallback)
    {
        var value = Text(configuration, key);
        return value is null
            ? fallback
            : long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
