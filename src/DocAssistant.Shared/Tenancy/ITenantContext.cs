namespace DocAssistant.Shared.Tenancy;

/// <summary>
/// The tenant selected for the current operation. See docs/decisions.md #14.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// Null when no tenant is selected (e.g. register, login). Tenant-owned queries
    /// then return no rows and inserting tenant-owned records fails.
    /// </summary>
    Guid? TenantId { get; }
}
