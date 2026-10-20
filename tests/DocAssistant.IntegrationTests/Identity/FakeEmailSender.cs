using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DocAssistant.Api.Modules.Email;

namespace DocAssistant.IntegrationTests.Identity;

// Stands in for the SMTP server (docs/decisions.md #70): keeps every e-mail the app hands
// it, and can be told to refuse them, as an unreachable server would. No e-mail leaves the
// test run.
internal sealed partial class FakeEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    // While set, SendAsync throws and nothing is kept.
    public bool Failing { get; set; }

    public IReadOnlyList<EmailMessage> Sent => [.. _sent];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (Failing)
        {
            throw new InvalidOperationException("The fake SMTP server refused the e-mail.");
        }

        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public IReadOnlyList<EmailMessage> SentTo(string email) =>
        _sent.Where(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase)).ToList();

    // The six digits of the newest code sent to the address.
    public string LastCodeFor(string email)
    {
        var message = SentTo(email).LastOrDefault()
            ?? throw new InvalidOperationException($"No e-mail was sent to {email}.");

        return SixDigits().Match(message.Body) is { Success: true } match
            ? match.Value
            : throw new InvalidOperationException($"The e-mail to {email} holds no six-digit code.");
    }

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex SixDigits();
}
