using System.Security.Claims;
using DocAssistant.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.Api.Modules.Identity;

// Runs for every request with a valid token (decisions #67): the token says who, in
// which organization and with which role; the database must still agree. One indexed
// lookup per request.
public static class MembershipCheck
{
    public const string FailureMessage = "The membership in this token no longer exists or has another role.";

    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;

        if (principal is null
            || !Guid.TryParse(principal.FindFirstValue(ClaimNames.UserId), out var userId)
            || !Guid.TryParse(principal.FindFirstValue(ClaimNames.TenantId), out var tenantId)
            || !Enum.TryParse<MembershipRole>(principal.FindFirstValue(ClaimNames.Role), ignoreCase: false, out var role))
        {
            context.Fail(FailureMessage);
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();

        // No tenant is selected yet at this point (it comes from the principal being
        // checked here), so the identity filter would match nothing (decisions #15).
        var stillMember = await db.Memberships
            .IgnoreQueryFilters()
            .AnyAsync(
                m => m.UserId == userId && m.TenantId == tenantId && m.Role == role,
                context.HttpContext.RequestAborted);

        if (!stillMember)
        {
            context.Fail(FailureMessage);
        }
    }
}
