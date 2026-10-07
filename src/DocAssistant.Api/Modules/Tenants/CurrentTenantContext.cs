using DocAssistant.Api.Modules.Identity;
using DocAssistant.Shared.Tenancy;

namespace DocAssistant.Api.Modules.Tenants;

// The one answer to "which tenant is this code working for?" (docs/decisions.md #14, #31).
//
//   Inside an HTTP request  -> the tenant in the caller's validated token, nothing else.
//   Outside a request       -> the tenant chosen with Select, or none.
//
// Scoped: one instance per request or per background scope, so a tenant can never carry
// over from one piece of work to the next.
public sealed class CurrentTenantContext(IHttpContextAccessor httpContextAccessor, JwtTenantContext jwtTenantContext)
    : ITenantContext, ITenantSelector
{
    private Guid? _selectedTenantId;

    public Guid? TenantId => IsInsideHttpRequest ? jwtTenantContext.TenantId : _selectedTenantId;

    private bool IsInsideHttpRequest => httpContextAccessor.HttpContext is not null;

    public void Select(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant id is required.", nameof(tenantId));
        }

        // If request code could pick a tenant, a single line would bypass tenant isolation.
        if (IsInsideHttpRequest)
        {
            throw new InvalidOperationException(
                "The tenant cannot be selected inside an HTTP request; it comes from the caller's token.");
        }

        // Switching mid-scope would leave a DbContext that has already read one tenant's
        // rows writing for another.
        if (_selectedTenantId is not null && _selectedTenantId != tenantId)
        {
            throw new InvalidOperationException(
                "A different tenant is already selected in this scope; create a new scope for each tenant.");
        }

        _selectedTenantId = tenantId;
    }
}
