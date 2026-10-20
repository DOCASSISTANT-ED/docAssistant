using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DocAssistant.Api.Modules.Email;

// Sends through an SMTP server with MailKit (docs/decisions.md #70). Microsoft no longer
// recommends .NET's own SmtpClient for new code and points to MailKit instead.
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    // A server that does not answer must not hold the request for long.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        // A client per e-mail: MailKit's client is not safe to share between requests, and
        // the e-mails are few.
        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };

        await client.ConnectAsync(settings.Host, settings.Port, SocketOptions(settings.Security), cancellationToken);

        if (!string.IsNullOrEmpty(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, settings.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }

    private static SecureSocketOptions SocketOptions(EmailSecurity security) => security switch
    {
        EmailSecurity.None => SecureSocketOptions.None,
        EmailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.StartTls,
    };
}
