using DocAssistant.Shared.Tenancy;

namespace DocAssistant.Api.Modules.Identity;

// Reads the tenant from the validated JWT of the current request.
// Claims only exist after JwtBearer has validated the token, so a forged or expired
// token never yields a tenant.
public sealed class JwtTenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    public Guid? TenantId
    {
        get
        {
            var value = httpContextAccessor.HttpContext?.User.FindFirst(ClaimNames.TenantId)?.Value;
            return Guid.TryParse(value, out var tenantId) ? tenantId : null;
        }
    }
}
