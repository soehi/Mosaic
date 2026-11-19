namespace Mosaic.Server.Emailing;

public enum EmailSecurity
{
    StartTls,
    SslOnConnect,
}

public sealed record EmailSettings
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 587;

    public EmailSecurity Security { get; init; } = EmailSecurity.StartTls;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string From { get; init; } = string.Empty;

    public string FromName { get; init; } = "Mosaic";

    public bool IsConfigured => Host.Length > 0 && From.Length > 0;

    public static EmailSecurity ParseSecurity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "starttls" => EmailSecurity.StartTls,
        "ssl" or "ssl-on-connect" or "implicit" => EmailSecurity.SslOnConnect,
        _ => throw new InvalidOperationException("MOSAIC_SMTP_SECURITY 只能是 starttls 或 ssl。"),
    };

    public static string SerializeSecurity(EmailSecurity security) =>
        security == EmailSecurity.SslOnConnect ? "ssl" : "starttls";
}

public sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody);

/// <summary>Non-secret projection of the mail configuration, safe to return to an admin.</summary>
public sealed record EmailSettingsView(
    string Host,
    int Port,
    string Security,
    string Username,
    bool HasPassword,
    string From,
    string FromName,
    bool Configured);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, EmailSettings settings, CancellationToken cancellationToken);
}

public sealed class EmailUnavailableException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
