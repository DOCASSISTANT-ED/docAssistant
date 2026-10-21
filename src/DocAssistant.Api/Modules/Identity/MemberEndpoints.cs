using System.Security.Claims;
using DocAssistant.Api.Data;
using DocAssistant.Shared.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.Api.Modules.Identity;

// The people of an organization (docs/decisions.md #67): admins see them and can remove
// one. The query filter on memberships keeps the list inside the caller's organization.
public static class MemberEndpoints
{
    public static IEndpointRouteBuilder MapMemberEndpoints(this IEndpointRouteBuilder app)
    {
        var members = app.MapGroup("/members")
            .RequireAuthorization(policy => policy.RequireRole(nameof(MembershipRole.Admin)));

        members.MapGet("/", List);
        members.MapDelete("/{userId:guid}", Remove);

        return app;
    }

    // Everyone in the organization, in the order they joined.
    private static async Task<Ok<List<MemberResponse>>> List(AppDbContext db, CancellationToken cancellationToken)
    {
        var members = await db.Memberships
            .AsNoTracking()
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.UserId, m.User.Email, m.Role, m.CreatedAt })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(members
            .Select(m => new MemberResponse(m.UserId, m.Email, m.Role.ToString(), m.CreatedAt))
            .ToList());
    }

    // Removes a person from the organization: their membership goes, their account and
    // their conversations stay (decisions #67). From the next request on, their token is
    // rejected (the membership check in IdentityModule). An admin cannot remove
    // themselves, and the last admin cannot be removed: the organization would have
    // nobody left to manage it.
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Remove(
        Guid userId,
        ClaimsPrincipal user,
        ITenantContext tenantContext,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(user.FindFirstValue(ClaimNames.UserId), out var callerId) && callerId == userId)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "You cannot remove yourself.");
        }

        // Two admins removing each other at the same moment would each count two admins and
        // each remove the other, leaving the organization without one. Removals in one
        // organization therefore take turns: the organization's row is locked until this
        // removal is done, and the next one counts after it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM tenants WHERE id = {tenantContext.TenantId} FOR UPDATE",
            cancellationToken);

        var membership = await db.Memberships
            .SingleOrDefaultAsync(m => m.UserId == userId, cancellationToken);

        if (membership is null)
        {
            return TypedResults.NotFound();
        }

        if (membership.Role == MembershipRole.Admin
            && await db.Memberships.CountAsync(m => m.Role == MembershipRole.Admin, cancellationToken) <= 1)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "The last admin cannot be removed.");
        }

        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.NoContent();
    }
}

public sealed record MemberResponse(Guid UserId, string Email, string Role, DateTimeOffset JoinedAt);
