namespace DocAssistant.Api.Modules.Identity;

// Identity table: not ITenantOwned, because registration writes it before any tenant
// is selected (docs/decisions.md #14).
public class User
{
    public Guid Id { get; set; }

    // As typed by the user; shown back in the UI.
    public required string Email { get; set; }

    // Upper-cased Email; unique across the system and used for lookups (decisions #12).
    public required string NormalizedEmail { get; set; }

    public required string PasswordHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<Membership> Memberships { get; set; } = [];

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();
}
