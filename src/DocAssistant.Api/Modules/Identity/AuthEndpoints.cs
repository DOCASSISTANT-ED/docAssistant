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
    // The unique index on tenants.normalized_name (TenantConfiguration, decisions #70).
    public const string CompanyNameIndex = "ix_tenants_normalized_name";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth");

        auth.MapPost("/register", Register);
        auth.MapPost("/login", Login);

        // No authorization: signing out must also work with an expired or missing token.
        auth.MapPost("/logout", Logout);

        auth.MapGet("/me", GetCurrentUser)
            .RequireAuthorization();

        return app;
    }

    // Creates a tenant, its first user and an Admin membership, then signs the user in.
    private static async Task<Results<Created<AccessToken>, ProblemHttpResult>> Register(
        RegisterRequest request,
        HttpContext http,
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique)
        {
            // decisions #70: company names are unique too.
            if (unique.ConstraintName == CompanyNameIndex)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "A company with this name is already registered.");
            }

            // Another request registered the same email between our check and the insert;
            // the unique index on normalized_email caught it.
            return EmailAlreadyRegistered();
        }

        var token = tokenIssuer.Issue(user.Id, user.Email, tenant.Id, membership.Role.ToString());
        SessionCookie.Append(http, token);
        return TypedResults.Created("/auth/me", token);
    }

    private static async Task<Results<Ok<AccessToken>, ProblemHttpResult>> Login(
        LoginRequest request,
        HttpContext http,
        AppDbContext db,
        IPasswordHasher<User> passwordHasher,
        JwtTokenIssuer tokenIssuer,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = User.NormalizeEmail(request.Email);

        // No tenant is selected before login (decisions #15).
        var user = await db.Users
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

        if (user is null)
        {
            // Verify against a dummy hash so an unknown email takes as long as a wrong
            // password; otherwise response time would reveal which emails are registered.
            passwordHasher.VerifyHashedPassword(TimingDummy.User, TimingDummy.PasswordHash, request.Password);
            return InvalidCredentials();
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);

        if (verification == PasswordVerificationResult.Failed)
        {
            return InvalidCredentials();
        }

        // decisions #70: a platform admin signs in without an organization, even when the
        // account also belongs to one, so that the two kinds of rights never mix.
        if (user.IsPlatformAdmin)
        {
            await RehashIfNeededAsync(user, request.Password, verification, db, passwordHasher, cancellationToken);

            var platformToken = tokenIssuer.Issue(user.Id, user.Email, tenantId: null, PlatformAdmin.Role);
            SessionCookie.Append(http, platformToken);
            return TypedResults.Ok(platformToken);
        }

        // Phase 1: every user has exactly one membership and signs in to it (decisions #13).
        var membership = await db.Memberships
            .IgnoreQueryFilters()
            .Where(m => m.UserId == user.Id)
            .OrderBy(m => m.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (membership is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "This account is not a member of any organization.");
        }

        await RehashIfNeededAsync(user, request.Password, verification, db, passwordHasher, cancellationToken);

        var token = tokenIssuer.Issue(user.Id, user.Email, membership.TenantId, membership.Role.ToString());
        SessionCookie.Append(http, token);
        return TypedResults.Ok(token);
    }

    // The stored hash uses an older format; upgrade it while we have the plain password.
    private static async Task RehashIfNeededAsync(
        User user,
        string password,
        PasswordVerificationResult verification,
        AppDbContext db,
        IPasswordHasher<User> passwordHasher,
        CancellationToken cancellationToken)
    {
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, password);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // Page scripts cannot delete an HttpOnly cookie, so the server does it (decisions #60).
    // The token itself stays valid until it expires; nothing is stored about it server-side.
    private static NoContent Logout(HttpContext http)
    {
        SessionCookie.Delete(http);
        return TypedResults.NoContent();
    }

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

    // Same response for unknown email and wrong password, so callers cannot tell which
    // emails are registered.
    private static ProblemHttpResult InvalidCredentials() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Invalid email or password.");

    private static class TimingDummy
    {
        public static readonly User User = new()
        {
            Email = string.Empty,
            NormalizedEmail = string.Empty,
            PasswordHash = string.Empty,
        };

        // Created with the same default hasher settings as the registered IPasswordHasher,
        // so verifying against it costs the same as verifying a real user's hash.
        public static readonly string PasswordHash =
            new PasswordHasher<User>().HashPassword(User, Guid.NewGuid().ToString());
    }
}

public sealed record CurrentUserResponse(string? UserId, string? Email, Guid? TenantId, string? Role);
