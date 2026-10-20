using System.ComponentModel.DataAnnotations;
using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Tenants;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocAssistant.Api.Modules.Identity;

// Registering as one of a company's admins with the company's registration code
// (docs/decisions.md #70). Open to anyone, but the code is always required: the company
// decides who its admins are by whom it gives the code to. The company is found from the
// code; no list of companies is ever shown.
public static class AdminRegistrationEndpoints
{
    public static IEndpointRouteBuilder MapAdminRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/register-admin", Register);

        return app;
    }

    // Makes the account and its Admin membership, then e-mails a verification code. Does
    // not sign in: the account can be used once the code is typed (POST /auth/verify-email).
    private static async Task<Results<Accepted<AdminRegistrationResponse>, ProblemHttpResult>> Register(
        AdminRegistrationRequest request,
        AppDbContext db,
        IPasswordHasher<User> passwordHasher,
        EmailVerificationService verifications,
        CancellationToken cancellationToken)
    {
        // No tenant before sign-in (decisions #15).
        var codeHash = RegistrationCode.Hash(request.RegistrationCode);
        var company = await db.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.RegistrationCodeHash == codeHash)
            .Select(t => new { t.Id, t.EmailDomains })
            .SingleOrDefaultAsync(cancellationToken);

        if (company is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The registration code is not valid.");
        }

        // The company's email rule, checked before the address is looked up, as when joining
        // by invitation (#70). Whoever holds the code may know the domains; the screen names them.
        if (!Tenant.AcceptsEmail(company.EmailDomains, request.Email))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "This company accepts only email addresses at its own domains.",
                extensions: new Dictionary<string, object?> { ["emailDomains"] = company.EmailDomains });
        }

        var normalizedEmail = User.NormalizeEmail(request.Email);

        // A person has one organization for now (decisions #13).
        var emailTaken = await db.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

        if (emailTaken)
        {
            return EmailAlreadyRegistered();
        }

        // Not verified yet (EmailVerifiedAt empty): it cannot sign in until the code is typed.
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = request.Email.Trim(),
            NormalizedEmail = normalizedEmail,
            PasswordHash = string.Empty,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        db.Users.Add(user);
        db.Memberships.Add(new Membership
        {
            Id = Guid.CreateVersion7(),
            User = user,
            TenantId = company.Id,
            Role = MembershipRole.Admin,
        });

        try
        {
            // One SaveChanges = one transaction: the account and its membership, or neither.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Someone registered the same email between the check and the insert.
            return EmailAlreadyRegistered();
        }

        // The account stays when the e-mail could not be sent (it would otherwise answer 409
        // when the person tries again); they ask for a new code on the verification screen.
        bool sent;
        try
        {
            sent = await verifications.SendCodeAsync(user, cancellationToken) == SendCodeResult.Sent;
        }
        catch (EmailNotSentException)
        {
            sent = false;
        }

        return TypedResults.Accepted((string?)null, new AdminRegistrationResponse(sent));
    }

    private static ProblemHttpResult EmailAlreadyRegistered() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "This email address is already registered.");
}

public sealed record AdminRegistrationRequest
{
    // XXXX-XXXX-XXXX; dashes, spaces and case do not count (RegistrationCode).
    [Required]
    [MaxLength(32)]
    public string RegistrationCode { get; init; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(UserConfiguration.EmailMaxLength)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [MinLength(PasswordRules.MinLength)]
    [MaxLength(PasswordRules.MaxLength)]
    public string Password { get; init; } = string.Empty;
}

// Whether the verification code went out; when not, the screen says so and offers to send
// a new one.
public sealed record AdminRegistrationResponse(bool VerificationSent);
