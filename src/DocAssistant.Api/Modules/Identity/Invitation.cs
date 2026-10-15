using System.Security.Cryptography;
using System.Text;
using DocAssistant.Api.Modules.Tenants;
using Microsoft.AspNetCore.WebUtilities;

namespace DocAssistant.Api.Modules.Identity;

// A link an admin hands to someone so they can join the organization (decisions #67).
// Identity table like memberships: not ITenantOwned and without RLS, because the person
// opening the link has no organization yet (decisions #15).
public class Invitation
{
    // decisions #67
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    // The role the person gets on joining.
    public MembershipRole Role { get; set; }

    // SHA-256 of the link's key, as lowercase hex. The key itself is never stored: like a
    // password, a copy of the database must not be enough to use a pending link.
    public required string TokenHash { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    // Set when someone joined with the link; a link works once.
    public DateTimeOffset? AcceptedAt { get; set; }

    public Guid? AcceptedByUserId { get; set; }

    // A new key for a link: 32 random bytes, written so that it fits in an address.
    public static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
