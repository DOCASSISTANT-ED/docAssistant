using System.ComponentModel.DataAnnotations;

namespace DocAssistant.Api.Modules.Email;

// The SMTP server the app sends e-mail through (docs/decisions.md #70): Mailpit in
// development, a real e-mail service in production.
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    // e.g. localhost for Mailpit.
    [Required]
    public string Host { get; init; } = string.Empty;

    // 1025 for Mailpit; 587 is the usual port of an e-mail service (with StartTls).
    [Range(1, 65535)]
    public int Port { get; init; } = 587;

    public EmailSecurity Security { get; init; } = EmailSecurity.StartTls;

    // Both empty: the server is used without signing in, as Mailpit allows. Never commit a
    // real password; production reads it from the environment (Email__Password).
    public string? Username { get; init; }

    public string? Password { get; init; }

    // The sender the recipient sees. An e-mail service sends only from an address on a
    // domain it has verified.
    [Required]
    [EmailAddress]
    public string FromAddress { get; init; } = string.Empty;

    public string FromName { get; init; } = "DocAssistant";
}

public enum EmailSecurity
{
    // No encryption: only for a server on this computer (Mailpit).
    None,

    // Connects unencrypted, then switches to TLS before anything is sent (port 587).
    StartTls,

    // TLS from the first byte (port 465).
    SslOnConnect,
}
