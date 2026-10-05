using DocAssistant.Api.Modules.Tenants;

namespace DocAssistant.Api.Modules.Identity;

// Links a user to a tenant with a role; a user may have several (decisions #12).
// Identity table: not ITenantOwned (decisions #14).
public class Membership
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public Guid TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public MembershipRole Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
