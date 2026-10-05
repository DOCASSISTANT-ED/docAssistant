namespace DocAssistant.Shared.Tenancy;

/// <summary>
/// Marks an entity as tenant data. Marked entities are filtered by the current tenant
/// on read and stamped with it on insert. See docs/decisions.md #14.
/// </summary>
/// <remarks>
/// Identity tables (tenants, users, memberships) must not implement this: registration
/// writes to them before any tenant is selected.
/// </remarks>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}
