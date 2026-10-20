namespace DocAssistant.Api.Modules.Identity;

// The code a new account confirms its e-mail address with (docs/decisions.md #70). One row
// per account at most: a new code replaces the old one, and the row is deleted once the
// address is verified. Identity table like invitations: not ITenantOwned and without RLS,
// because the account has no organization to sign in to yet (decisions #15).
public class EmailVerification
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    // SHA-256 of the six digits, as lowercase hex, as with invitation keys (#67). Six
    // digits are few enough to try them all against a stolen hash; what protects a code is
    // its short life and the attempt limit, the hash only keeps it out of plain sight.
    public required string CodeHash { get; set; }

    // When this code was sent; a new one can be asked for only a while later.
    public DateTimeOffset SentAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    // Wrong codes entered for this code; at the limit the code no longer works.
    public int FailedAttempts { get; set; }

    // Codes sent to the account within an hour, counted from WindowStartedAt. Limits how
    // often a mailbox can be sent codes, and with each new code the attempts start over.
    public DateTimeOffset WindowStartedAt { get; set; }

    public int SendsInWindow { get; set; }
}
