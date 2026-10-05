using System.Security.Claims;
using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Tenants;
using DocAssistant.Shared.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    // Creates a tenant, its first user and an Admin membership, then signs the user in.
    private static async Task<Results<Created<AccessToken>, ProblemHttpResult>> Register(
        RegisterRequest request,
        AppDbContext db,
        IPasswordHasher<User> passwordHasher,
        JwtTokenIssuer tokenIssuer,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = User.NormalizeEmail(request.Email);

        // No tenant is selected during registration, so identity filters are bypassed
        // on purpose (decisions #15).
        var emailTaken = await db.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

        if (emailTaken)
        {
            return EmailAlreadyRegistered();
        }

        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Name = request.CompanyName.Trim(),
        };

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = request.Email.Trim(),
            NormalizedEmail = normalizedEmail,
            PasswordHash = string.Empty,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        var membership = new Membership
        {
            Id = Guid.CreateVersion7(),
            User = user,
            Tenant = tenant,
            Role = MembershipRole.Admin,
        };

        db.Tenants.Add(tenant);
        db.Users.Add(user);
        db.Memberships.Add(membership);

        try
        {
            // One SaveChanges = one transaction: all three rows or none (decisions #11).
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another request registered the same email between our check and the insert;
            // the unique index on normalized_email caught it.
            return EmailAlreadyRegistered();
        }

        var token = tokenIssuer.Issue(user.Id, user.Email, tenant.Id, membership.Role.ToString());
        return TypedResults.Created("/auth/me", token);
    }

    private static IResult Login(LoginRequest request) =>
        Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "Not implemented yet.");

    private static CurrentUserResponse GetCurrentUser(ClaimsPrincipal user, ITenantContext tenantContext) =>
        new(
            UserId: user.FindFirstValue(ClaimNames.UserId),
            Email: user.FindFirstValue(ClaimNames.Email),
            TenantId: tenantContext.TenantId,
            Role: user.FindFirstValue(ClaimNames.Role));

    private static ProblemHttpResult EmailAlreadyRegistered() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "This email address is already registered.");
}

public sealed record CurrentUserResponse(string? UserId, string? Email, Guid? TenantId, string? Role);
