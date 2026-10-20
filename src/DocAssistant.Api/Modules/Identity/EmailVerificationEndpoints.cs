using System.ComponentModel.DataAnnotations;
using DocAssistant.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.Api.Modules.Identity;

// Typing the e-mailed code, and asking for a new one (docs/decisions.md #70). No sign-in:
// the account cannot sign in until it is verified. The account is named by its e-mail,
// and no answer tells whether an address has an account.
public static class EmailVerificationEndpoints
{
    public static IEndpointRouteBuilder MapEmailVerificationEndpoints(this IEndpointRouteBuilder app)
    {
        var verification = app.MapGroup("/auth/verify-email");

        verification.MapPost("", Verify);
        verification.MapPost("/resend", Resend);

        return app;
    }

    // The right code verifies the account and signs the person in, as registering and
    // joining do (#60); anything else is the same 400.
    private static async Task<Results<Ok<AccessToken>, ProblemHttpResult>> Verify(
        VerifyEmailRequest request,
        HttpContext http,
        AppDbContext db,
        EmailVerificationService verifications,
        JwtTokenIssuer tokenIssuer,
        CancellationToken cancellationToken)
    {
        // No tenant before sign-in (decisions #15).
        var user = await FindUserAsync(db, request.Email, cancellationToken);

        if (user is null || await verifications.VerifyAsync(user, request.Code, cancellationToken) != VerifyCodeResult.Verified)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The code is wrong or no longer valid.");
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

        var token = tokenIssuer.Issue(user.Id, user.Email, membership.TenantId, membership.Role.ToString());
        SessionCookie.Append(http, token);
        return TypedResults.Ok(token);
    }

    // Always 204, whether or not the address has an account, is already verified or asked
    // too soon (the limits are in EmailVerificationService): the answer says nothing about
    // the address. Only an e-mail that could not be sent is told, so the person knows to try
    // again later.
    private static async Task<Results<NoContent, ProblemHttpResult>> Resend(
        ResendVerificationRequest request,
        AppDbContext db,
        EmailVerificationService verifications,
        CancellationToken cancellationToken)
    {
        var user = await FindUserAsync(db, request.Email, cancellationToken);

        if (user is not null)
        {
            try
            {
                await verifications.SendCodeAsync(user, cancellationToken);
            }
            catch (EmailNotSentException)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The e-mail could not be sent. Try again later.");
            }
        }

        return TypedResults.NoContent();
    }

    // Tracked: verifying sets EmailVerifiedAt on it. Platform admins are made on the server
    // and verified there (PlatformAdmin), so they never need a code.
    private static Task<User?> FindUserAsync(AppDbContext db, string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = User.NormalizeEmail(email);

        return db.Users
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail && !u.IsPlatformAdmin, cancellationToken);
    }
}

public sealed record VerifyEmailRequest
{
    [Required]
    [MaxLength(UserConfiguration.EmailMaxLength)]
    public string Email { get; init; } = string.Empty;

    // Six digits; spaces are allowed and ignored ("123 456").
    [Required]
    [MaxLength(20)]
    public string Code { get; init; } = string.Empty;
}

public sealed record ResendVerificationRequest
{
    [Required]
    [MaxLength(UserConfiguration.EmailMaxLength)]
    public string Email { get; init; } = string.Empty;
}
