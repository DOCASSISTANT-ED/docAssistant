namespace DocAssistant.Api.Modules.Email;

// Sends an e-mail (docs/decisions.md #70). Callers do not know how: over SMTP to Mailpit
// in development, to an e-mail service in production; tests replace it with a fake.
public interface IEmailSender
{
    // Throws when the e-mail could not be handed to the server; the caller decides what
    // the user is told.
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

// Plain text only: the e-mails are short notices such as a verification code.
public sealed record EmailMessage(string To, string Subject, string Body);
