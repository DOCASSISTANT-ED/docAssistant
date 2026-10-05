using System.Security.Claims;
using DocAssistant.Shared.Tenancy;

namespace DocAssistant.Api.Modules.Identity;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth");

        auth.MapPost("/register", Register);
        auth.MapPost("/login", Login);

        auth.MapGet("/me", GetCurrentUser)
            .RequireAuthorization();

        return app;
    }

    // Filled in by the next commits; validation already runs before these handlers.
    private static IResult Register(RegisterRequest request) =>
        Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "Not implemented yet.");

    private static IResult Login(LoginRequest request) =>
        Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "Not implemented yet.");

    private static CurrentUserResponse GetCurrentUser(ClaimsPrincipal user, ITenantContext tenantContext) =>
        new(
            UserId: user.FindFirstValue(ClaimNames.UserId),
            Email: user.FindFirstValue(ClaimNames.Email),
            TenantId: tenantContext.TenantId,
            Role: user.FindFirstValue(ClaimNames.Role));
}

public sealed record CurrentUserResponse(string? UserId, string? Email, Guid? TenantId, string? Role);
