using System.Security.Claims;
using DocAssistant.Shared.Tenancy;

namespace DocAssistant.Api.Modules.Identity;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth");

        auth.MapGet("/me", GetCurrentUser)
            .RequireAuthorization();

        return app;
    }

    private static CurrentUserResponse GetCurrentUser(ClaimsPrincipal user, ITenantContext tenantContext) =>
        new(
            UserId: user.FindFirstValue(ClaimNames.UserId),
            Email: user.FindFirstValue(ClaimNames.Email),
            TenantId: tenantContext.TenantId,
            Role: user.FindFirstValue(ClaimNames.Role));
}

public sealed record CurrentUserResponse(string? UserId, string? Email, Guid? TenantId, string? Role);
