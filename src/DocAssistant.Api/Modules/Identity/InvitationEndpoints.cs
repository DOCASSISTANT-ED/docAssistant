using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Tenants;
using DocAssistant.Shared.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocAssistant.Api.Modules.Identity;

// Inviting people into an organization with a link (docs/decisions.md #67). Admins
// create, list and revoke invitations; whoever has a link can look at it and join,
// without being signed in.
public static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        var invitations = app.MapGroup("/invitations");

        var admins = invitations.MapGroup("")
            .RequireAuthorization(policy => policy.RequireRole(nameof(MembershipRole.Admin)));

        admins.MapPost("/", Create);
        admins.MapGet("/", ListPending);
        admins.MapDelete("/{id:guid}", Revoke);

        // The link's key is the credential here; no sign-in.
        invitations.MapGet("/{token}", Preview);
        invitations.MapPost("/{token}/accept", Accept);

        return app;
    }

    // A new link. The key is in this answer only: the database keeps its hash.
    private static async Task<Results<Created<InvitationCreatedResponse>, ProblemHttpResult>> Create(
        CreateInvitationRequest request,
        ClaimsPrincipal user,
        ITenantContext tenantContext,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not { } tenantId
            || !Guid.TryParse(user.FindFirstValue(ClaimNames.UserId), out var userId))
        {
            return Problem(StatusCodes.Status403Forbidden, "The access token does not identify a user and an organization.");
        }

        if (!TryParseRole(request.Role, out var role))
        {
            return Problem(StatusCodes.Status400BadRequest, "The role must be \"Member\" or \"Admin\".");
        }

        var token = Invitation.NewToken();
        var now = timeProvider.GetUtcNow();

        var invitation = new Invitation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Role = role,
            TokenHash = Invitation.HashToken(token),
            CreatedByUserId = userId,
            CreatedAt = now,
            ExpiresAt = now + Invitation.Lifetime,
        };

        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created(
            $"/invitations/{invitation.Id}",
            new InvitationCreatedResponse(invitation.Id, token, role.ToString(), invitation.ExpiresAt));
    }

    // The organization's invitations that can still be used, newest first. The query
    // filter keeps other organizations' out.
    private static async Task<Ok<List<InvitationResponse>>> ListPending(
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var invitations = await db.Invitations
            .AsNoTracking()
            .Where(i => i.AcceptedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new { i.Id, i.Role, i.CreatedAt, i.ExpiresAt })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(invitations
            .Select(i => new InvitationResponse(i.Id, i.Role.ToString(), i.CreatedAt, i.ExpiresAt))
            .ToList());
    }

    // Revoking deletes the invitation: its link stops working at once. An invitation that
    // was already used is not touched; the person who joined stays (removing them is
    // DELETE /members).
    private static async Task<Results<NoContent, NotFound>> Revoke(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var deleted = await db.Invitations
            .Where(i => i.Id == id && i.AcceptedAt == null)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    // What the link is for, before joining: the organization and the role. A link that
    // is unknown, used, revoked or expired is "not found" alike.
    private static async Task<Results<Ok<InvitationPreviewResponse>, NotFound>> Preview(
        string token,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var invitation = await FindUsable(db, token, timeProvider.GetUtcNow())
            .Select(i => new { CompanyName = i.Tenant.Name, i.Role, i.ExpiresAt })
            .SingleOrDefaultAsync(cancellationToken);

        return invitation is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new InvitationPreviewResponse(invitation.CompanyName, invitation.Role.ToString(), invitation.ExpiresAt));
    }

    // Joins with the link: a new user and their membership, in one transaction with the
    // link being used up; then the address is verified with an e-mailed code (#70).
    private static async Task<Results<Accepted<JoinedResponse>, NotFound, ProblemHttpResult>> Accept(
        string token,
        AcceptInvitationRequest request,
        AppDbContext db,
        IPasswordHasher<User> passwordHasher,
        EmailVerificationService verifications,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var normalizedEmail = User.NormalizeEmail(request.Email);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var invitation = await FindUsable(db, token, now)
            .Select(i => new { i.Id, i.TenantId, i.Role, i.Tenant.EmailDomains })
            .SingleOrDefaultAsync(cancellationToken);

        if (invitation is null)
        {
            return TypedResults.NotFound();
        }

        // decisions #70: a company with an email rule takes only its own addresses, also by
        // invitation. Checked before the address is looked up, so the answer says nothing
        // about whether it is registered. The domains are in the answer: whoever holds the
        // link may know them, and the join screen names them.
        if (!Tenant.AcceptsEmail(invitation.EmailDomains, request.Email))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "This company accepts only email addresses at its own domains.",
                extensions: new Dictionary<string, object?> { ["emailDomains"] = invitation.EmailDomains });
        }

        // A person has one organization for now (decisions #13): an email that is already
        // registered cannot join another one.
        var emailTaken = await db.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

        if (emailTaken)
        {
            return EmailAlreadyRegistered();
        }

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
            TenantId = invitation.TenantId,
            Role = invitation.Role,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Someone registered the same email between the check and the insert.
            return EmailAlreadyRegistered();
        }

        // The link is used up only if nobody else used it in the meantime; otherwise this
        // whole join is undone (the transaction is not committed).
        var claimed = await db.Invitations
            .IgnoreQueryFilters()
            .Where(i => i.Id == invitation.Id && i.AcceptedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(i => i.AcceptedAt, now)
                    .SetProperty(i => i.AcceptedByUserId, user.Id),
                cancellationToken);

        if (claimed == 0)
        {
            return TypedResults.NotFound();
        }

        await transaction.CommitAsync(cancellationToken);

        // decisions #70: a new account proves its address before it is used, as an admin's
        // does at registration. No sign-in here: the code is e-mailed, and typing it
        // (POST /auth/verify-email) signs the person in to this organization with this role.
        // The account stays when the e-mail could not be sent (joining again would only
        // answer 404, the link is used up); the person asks for a new code on that screen.
        bool sent;
        try
        {
            sent = await verifications.SendCodeAsync(user, cancellationToken) == SendCodeResult.Sent;
        }
        catch (EmailNotSentException)
        {
            sent = false;
        }

        return TypedResults.Accepted((string?)null, new JoinedResponse(sent));
    }

    // Not used, not expired; looked up by the hash of the key, without a tenant.
    private static IQueryable<Invitation> FindUsable(AppDbContext db, string token, DateTimeOffset now)
    {
        var hash = Invitation.HashToken(token);

        return db.Invitations
            .IgnoreQueryFilters()
            .Where(i => i.TokenHash == hash && i.AcceptedAt == null && i.ExpiresAt > now);
    }

    private static bool TryParseRole(string? value, out MembershipRole role)
    {
        if (string.IsNullOrEmpty(value))
        {
            role = MembershipRole.Member;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: false, out role) && Enum.IsDefined(role);
    }

    private static ProblemHttpResult EmailAlreadyRegistered() =>
        Problem(StatusCodes.Status409Conflict, "This email address is already registered.");

    private static ProblemHttpResult Problem(int statusCode, string title) =>
        TypedResults.Problem(statusCode: statusCode, title: title);
}

// Role is "Member" (the default) or "Admin".
public sealed record CreateInvitationRequest
{
    public string? Role { get; init; }
}

// The same rules as registration (RegisterRequest).
public sealed record AcceptInvitationRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(UserConfiguration.EmailMaxLength)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [MinLength(PasswordRules.MinLength)]
    [MaxLength(PasswordRules.MaxLength)]
    public string Password { get; init; } = string.Empty;
}

// Token is the key for the link; it is shown here once and never again.
public sealed record InvitationCreatedResponse(Guid Id, string Token, string Role, DateTimeOffset ExpiresAt);

public sealed record InvitationResponse(Guid Id, string Role, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record InvitationPreviewResponse(string CompanyName, string Role, DateTimeOffset ExpiresAt);

// The answer to joining (decisions #70): whether the verification code went out; when not,
// the verification screen offers a new one at once. As AdminRegistrationResponse.
public sealed record JoinedResponse(bool VerificationSent);
