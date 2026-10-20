using System.Security.Cryptography;
using System.Text;
using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Email;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.Api.Modules.Identity;

// Confirms that a new account's e-mail address belongs to the person (docs/decisions.md
// #70): a six-digit code is sent to the address and typed back. Used by everything that
// makes an account: an admin's registration and joining by invitation.
public sealed class EmailVerificationService(
    AppDbContext db,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    ILogger<EmailVerificationService> logger)
{
    // decisions #70
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);

    public const int MaxAttempts = 5;

    // How often a mailbox can be sent a code. Without a limit anyone could flood someone's
    // inbox and use up the e-mail service's daily quota; and since every new code starts
    // its attempts over, the limit also keeps a code out of reach of guessing.
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    public const int MaxSendsPerHour = 5;

    private static readonly TimeSpan SendWindow = TimeSpan.FromHours(1);

    public const int CodeLength = 6;

    // Sends the account a new code, which replaces any earlier one. Saves its own changes.
    // Throws EmailNotSentException when the e-mail could not be sent; then nothing is
    // saved, so the person can ask again at once.
    public async Task<SendCodeResult> SendCodeAsync(User user, CancellationToken cancellationToken = default)
    {
        if (user.EmailVerifiedAt is not null)
        {
            return SendCodeResult.AlreadyVerified;
        }

        var now = timeProvider.GetUtcNow();

        var verification = await db.EmailVerifications
            .SingleOrDefaultAsync(v => v.UserId == user.Id, cancellationToken);

        if (verification is null)
        {
            verification = new EmailVerification
            {
                Id = Guid.CreateVersion7(),
                UserId = user.Id,
                CodeHash = string.Empty,
                WindowStartedAt = now,
            };
            db.EmailVerifications.Add(verification);
        }
        else
        {
            if (now - verification.SentAt < ResendCooldown)
            {
                return SendCodeResult.TooSoon;
            }

            // A new hour begins its own count.
            if (now - verification.WindowStartedAt >= SendWindow)
            {
                verification.WindowStartedAt = now;
                verification.SendsInWindow = 0;
            }

            if (verification.SendsInWindow >= MaxSendsPerHour)
            {
                return SendCodeResult.TooMany;
            }
        }

        var code = NewCode();

        verification.CodeHash = HashCode(code);
        verification.SentAt = now;
        verification.ExpiresAt = now + CodeLifetime;
        verification.FailedAttempts = 0;
        verification.SendsInWindow++;

        // The e-mail first: a code that was never sent must not start the cooldown.
        try
        {
            await emailSender.SendAsync(CodeMessage(user.Email, code), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Forget the unsaved changes, so a later SaveChanges in this request (the
            // caller's) does not store a code nobody received.
            if (db.Entry(verification).State == EntityState.Added)
            {
                db.Entry(verification).State = EntityState.Detached;
            }
            else
            {
                await db.Entry(verification).ReloadAsync(cancellationToken);
            }

            logger.LogError(exception, "The verification code could not be e-mailed to user {UserId}.", user.Id);
            throw new EmailNotSentException(exception);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two requests sent a first code at the same moment and the unique index on
            // user_id kept one; the other's code was e-mailed but is not valid. The person
            // has the valid one too, sent a moment apart.
            db.Entry(verification).State = EntityState.Detached;
            return SendCodeResult.TooSoon;
        }

        return SendCodeResult.Sent;
    }

    // Checks a typed code. On the right one the account is verified and the code deleted.
    public async Task<VerifyCodeResult> VerifyAsync(User user, string code, CancellationToken cancellationToken = default)
    {
        if (user.EmailVerifiedAt is not null)
        {
            return VerifyCodeResult.Invalid;
        }

        var typed = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (typed.Length != CodeLength || !typed.All(char.IsAsciiDigit))
        {
            return VerifyCodeResult.Invalid;
        }

        var now = timeProvider.GetUtcNow();

        // An attempt is used up before the code is compared, in one statement: two guesses
        // sent at the same moment cannot both slip under the limit.
        var counted = await db.EmailVerifications
            .Where(v => v.UserId == user.Id && v.FailedAttempts < MaxAttempts && v.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.FailedAttempts, v => v.FailedAttempts + 1), cancellationToken);

        if (counted == 0)
        {
            return VerifyCodeResult.Invalid;
        }

        var verification = await db.EmailVerifications
            .AsNoTracking()
            .SingleOrDefaultAsync(v => v.UserId == user.Id, cancellationToken);

        if (verification is null || !SameHash(verification.CodeHash, HashCode(typed)))
        {
            return VerifyCodeResult.Invalid;
        }

        user.EmailVerifiedAt = now;
        db.EmailVerifications.Remove(verification);

        // One SaveChanges = one transaction: the account is verified and the code gone, or
        // neither (decisions #11).
        await db.SaveChangesAsync(cancellationToken);

        return VerifyCodeResult.Verified;
    }

    // 000000 to 999999, from the operating system's secure random source.
    private static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static string HashCode(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    // Compares in constant time, so the time an answer takes says nothing about how much
    // of a guess was right.
    private static bool SameHash(string stored, string computed) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored), Encoding.ASCII.GetBytes(computed));

    private static EmailMessage CodeMessage(string to, string code) => new(
        to,
        "DocAssistant doğrulama kodunuz",
        $"""
        Merhaba,

        DocAssistant hesabınızı doğrulamak için kodunuz:

        {code}

        Kod {CodeLifetime.TotalMinutes:0} dakika geçerlidir. Bu isteği siz yapmadıysanız bu e-postayı dikkate almayın; doğrulanmayan hesapla giriş yapılamaz.
        """.ReplaceLineEndings("\n"));
}

public enum SendCodeResult
{
    Sent,

    // The account is verified; there is nothing to send.
    AlreadyVerified,

    // The last code went out less than ResendCooldown ago.
    TooSoon,

    // MaxSendsPerHour codes went out within the hour.
    TooMany,
}

public enum VerifyCodeResult
{
    Verified,

    // Wrong, expired, out of attempts, never sent or already used: told apart nowhere, so
    // an answer says nothing about the account.
    Invalid,
}

// The verification e-mail did not reach the SMTP server (decisions #70); the request is
// answered 503 and the person can ask for a new code.
public sealed class EmailNotSentException(Exception inner)
    : Exception("The verification e-mail could not be sent.", inner);
