using System.Net;
using System.Text.Json;
using DocAssistant.Api.Modules.Chat;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Identity;

// GET /members and DELETE /members/{userId} (docs/decisions.md #67): an admin sees the
// people of the organization and removes one. Removing takes away the membership only;
// the account and the person's conversations stay. An admin cannot remove themselves, and
// the organization is never left without an admin.
[Collection(DatabaseCollection.Name)]
public class MemberTests(PostgresFixture database)
{
    // ---- The list ----

    [Fact]
    public async Task ANewOrganizationHasItsFounderAsItsOnlyMember()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        var member = Assert.Single(await Invitations.MemberListAsync(client, admin.Token));

        Assert.Equal(admin.UserId, member.UserId);
        Assert.Equal(admin.Email, member.Email);
        Assert.Equal("Admin", member.Role);
    }

    [Fact]
    public async Task TheListHasEveryoneInTheOrderTheyJoined()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var first = await JoinAsync(client, admin, role: null);
        var second = await JoinAsync(client, admin, role: "Admin");

        var listed = await Invitations.MemberListAsync(client, admin.Token);

        Assert.Equal([admin.Email, first, second], listed.Select(m => m.Email));
        Assert.Equal(["Admin", "Member", "Admin"], listed.Select(m => m.Role));
    }

    [Fact]
    public async Task AnotherOrganizationsPeopleAreNotListed()
    {
        using var client = database.Api.CreateClient();
        var ofA = await Invitations.RegisterAdminAsync(client);
        var ofB = await Invitations.RegisterAdminAsync(client);
        await JoinAsync(client, ofB, role: null);

        var listed = await Invitations.MemberListAsync(client, ofA.Token);

        Assert.Equal([ofA.UserId], listed.Select(m => m.UserId));
    }

    // Who someone is and since when; nothing of their account's secrets.
    [Fact]
    public async Task AnEntryHasTheDecidedFieldsAndNoPasswordHash()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        using var response = await Invitations.MembersAsync(client, admin.Token);

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var entry = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(
            ["email", "joinedAt", "role", "userId"],
            entry.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("AQAAAA", body);
    }

    [Fact]
    public async Task AMemberWhoIsNotAnAdminCannotSeeTheList()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Member);

        using var response = await Invitations.MembersAsync(client, member.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheListNeedsASession()
    {
        using var client = database.Api.CreateClient();

        using var response = await Invitations.MembersAsync(client, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Removing ----

    [Fact]
    public async Task RemovingTakesTheMembershipAwayAndLeavesTheAccount()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Member);

        using var response = await Invitations.RemoveAsync(client, admin.Token, member.UserId);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Invitations.MembershipAsync(database, member.UserId, admin.TenantId));
        Assert.NotNull(await Invitations.UserByEmailAsync(database, member.Email));
        Assert.Equal([admin.UserId], (await Invitations.MemberListAsync(client, admin.Token)).Select(m => m.UserId));
    }

    // The conversations stay with the account (#67); nobody else can read them (#57).
    [Fact]
    public async Task ThePersonsConversationsStay()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Member);
        var conversationId = Guid.CreateVersion7();
        await using (var db = database.CreateOwnerDbContext(admin.TenantId))
        {
            db.Conversations.Add(new Conversation
            {
                Id = conversationId,
                UserId = member.UserId,
                Title = "Yıllık izin kaç gün?",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using (await Invitations.RemoveAsync(client, admin.Token, member.UserId))
        {
        }

        await using var check = database.CreateOwnerDbContext();
        Assert.True(await check.Conversations.IgnoreQueryFilters().AnyAsync(c => c.Id == conversationId));
    }

    // Removing is about this organization; a person in another one stays there.
    [Fact]
    public async Task RemovingLeavesThePersonsOtherOrganizationsAlone()
    {
        using var client = database.Api.CreateClient();
        var ofA = await Invitations.RegisterAdminAsync(client);
        var ofB = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, ofA.TenantId, MembershipRole.Member);
        await using (var db = database.CreateOwnerDbContext(ofB.TenantId))
        {
            db.Memberships.Add(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = member.UserId,
                TenantId = ofB.TenantId,
                Role = MembershipRole.Member,
            });
            await db.SaveChangesAsync();
        }

        using (await Invitations.RemoveAsync(client, ofA.Token, member.UserId))
        {
        }

        Assert.Null(await Invitations.MembershipAsync(database, member.UserId, ofA.TenantId));
        Assert.NotNull(await Invitations.MembershipAsync(database, member.UserId, ofB.TenantId));
    }

    [Fact]
    public async Task AnAdminCanRemoveAnotherAdmin()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var other = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Admin);

        using var response = await Invitations.RemoveAsync(client, admin.Token, other.UserId);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Invitations.MembershipAsync(database, other.UserId, admin.TenantId));
    }

    [Fact]
    public async Task AnAdminCannotRemoveThemselves()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Admin);

        using var response = await Invitations.RemoveAsync(client, admin.Token, admin.UserId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(await Invitations.MembershipAsync(database, admin.UserId, admin.TenantId));
    }

    // One after the other, two admins cannot leave the organization without one: the first
    // removal ends the other's access (the membership check), so the second is refused.
    // At the same moment they could; see the PR notes.
    [Fact]
    public async Task TwoAdminsRemovingEachOtherInTurnLeaveOneAdmin()
    {
        using var client = database.Api.CreateClient();
        var first = await Invitations.RegisterAdminAsync(client);
        var second = await TestAccounts.AddMemberAsync(database, first.TenantId, MembershipRole.Admin);

        using var firstRemovesSecond = await Invitations.RemoveAsync(client, first.Token, second.UserId);
        using var secondRemovesFirst = await Invitations.RemoveAsync(client, second.Token, first.UserId);

        Assert.Equal(HttpStatusCode.NoContent, firstRemovesSecond.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, secondRemovesFirst.StatusCode);
        var admins = (await Invitations.MemberListAsync(client, first.Token)).Where(m => m.Role == "Admin");
        Assert.Equal([first.UserId], admins.Select(m => m.UserId));
    }

    // The worst case: an admin of one organization removing someone from another.
    [Fact]
    public async Task AnAdminOfAnotherOrganizationGetsNotFoundAndThePersonStays()
    {
        using var client = database.Api.CreateClient();
        var owner = await Invitations.RegisterAdminAsync(client);
        var stranger = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, owner.TenantId, MembershipRole.Member);

        using var response = await Invitations.RemoveAsync(client, stranger.Token, member.UserId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await Invitations.MembershipAsync(database, member.UserId, owner.TenantId));
    }

    [Fact]
    public async Task APersonWhoIsNotInTheOrganizationIsNotFound()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        using var response = await Invitations.RemoveAsync(client, admin.Token, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AMemberWhoIsNotAnAdminCannotRemoveAnyone()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Member);
        var colleague = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Member);

        using var response = await Invitations.RemoveAsync(client, member.Token, colleague.UserId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Invitations.MembershipAsync(database, colleague.UserId, admin.TenantId));
    }

    [Fact]
    public async Task RemovingNeedsASession()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        using var response = await Invitations.RemoveAsync(client, token: null, admin.UserId);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Joins the organization through a link, as a person would; returns their email.
    private static async Task<string> JoinAsync(HttpClient client, TestMember admin, string? role)
    {
        var created = await Invitations.CreatedAsync(client, admin.Token, role);
        var email = TestAccounts.NewEmail();

        using var response = await Invitations.AcceptAsync(client, created.Token, email);
        response.EnsureSuccessStatusCode();

        return email;
    }
}
