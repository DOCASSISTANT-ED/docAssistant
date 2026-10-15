using System.Net;
using System.Text.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;

namespace DocAssistant.IntegrationTests.Identity;

// GET /invitations and DELETE /invitations/{id} (docs/decisions.md #67): an admin sees the
// organization's invitations that can still be used and can revoke one, after which its
// link stops working at once. An invitation that was already used is not touched: the
// person who joined stays (removing them is DELETE /members).
[Collection(DatabaseCollection.Name)]
public class InvitationListAndRevokeTests(PostgresFixture database)
{
    // ---- The list ----

    [Fact]
    public async Task ANewOrganizationHasNoInvitations()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        Assert.Empty(await Invitations.ListedAsync(client, admin.Token));
    }

    [Fact]
    public async Task TheListHasThePendingInvitationsNewestFirst()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var oldest = await Invitations.CreatedAsync(client, admin.Token);
        var middle = await Invitations.CreatedAsync(client, admin.Token, "Admin");
        var newest = await Invitations.CreatedAsync(client, admin.Token);

        var listed = await Invitations.ListedAsync(client, admin.Token);

        Assert.Equal([newest.Id, middle.Id, oldest.Id], listed.Select(i => i.Id));
        Assert.Equal(["Member", "Admin", "Member"], listed.Select(i => i.Role));
    }

    // Only links that still work: used and expired ones have nothing left to manage.
    [Fact]
    public async Task UsedAndExpiredInvitationsAreNotListed()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var used = await Invitations.CreatedAsync(client, admin.Token);
        var expired = await Invitations.CreatedAsync(client, admin.Token);
        var pending = await Invitations.CreatedAsync(client, admin.Token);
        using (await Invitations.AcceptAsync(client, used.Token, TestAccounts.NewEmail()))
        {
        }
        await Invitations.ExpireAsync(database, expired.Id);

        var listed = await Invitations.ListedAsync(client, admin.Token);

        Assert.Equal([pending.Id], listed.Select(i => i.Id));
    }

    [Fact]
    public async Task AnotherOrganizationsInvitationsAreNotListed()
    {
        using var client = database.Api.CreateClient();
        var ofA = await Invitations.RegisterAdminAsync(client);
        var ofB = await Invitations.RegisterAdminAsync(client);
        var invitationOfA = await Invitations.CreatedAsync(client, ofA.Token);
        var invitationOfB = await Invitations.CreatedAsync(client, ofB.Token);

        Assert.Equal([invitationOfA.Id], (await Invitations.ListedAsync(client, ofA.Token)).Select(i => i.Id));
        Assert.Equal([invitationOfB.Id], (await Invitations.ListedAsync(client, ofB.Token)).Select(i => i.Id));
    }

    // The key is shown once, when the link is made; the list has no way to give it again.
    [Fact]
    public async Task AnEntryHasNoKeyAndNoHashOfIt()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        await Invitations.CreatedAsync(client, admin.Token);

        using var response = await Invitations.ListAsync(client, admin.Token);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(
            ["createdAt", "expiresAt", "id", "role"],
            entry.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AMemberWhoIsNotAnAdminCannotSeeTheList()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Member);

        using var response = await Invitations.ListAsync(client, member.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheListNeedsASession()
    {
        using var client = database.Api.CreateClient();

        using var response = await Invitations.ListAsync(client, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Revoking ----

    [Fact]
    public async Task ARevokedLinkStopsWorkingAtOnce()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);

        using var revoked = await Invitations.RevokeAsync(client, admin.Token, created.Id);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var preview = await Invitations.PreviewAsync(client, created.Token);
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.Empty(await Invitations.ListedAsync(client, admin.Token));
        Assert.Null(await Invitations.ReadAsync(database, created.Id));
    }

    // Nothing to protect any more, but nothing in the way of tidying up either.
    [Fact]
    public async Task AnExpiredInvitationCanBeRevoked()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        await Invitations.ExpireAsync(database, created.Id);

        using var revoked = await Invitations.RevokeAsync(client, admin.Token, created.Id);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
    }

    // The worst case: an admin of one organization cutting another's links.
    [Fact]
    public async Task AnAdminOfAnotherOrganizationGetsNotFoundAndTheLinkStillWorks()
    {
        using var client = database.Api.CreateClient();
        var owner = await Invitations.RegisterAdminAsync(client);
        var stranger = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, owner.Token);

        using var revoked = await Invitations.RevokeAsync(client, stranger.Token, created.Id);

        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        Assert.NotNull(await Invitations.ReadAsync(database, created.Id));
        using var preview = await Invitations.PreviewAsync(client, created.Token);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
    }

    [Fact]
    public async Task AnInvitationThatDoesNotExistIsNotFound()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        using var revoked = await Invitations.RevokeAsync(client, admin.Token, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    [Fact]
    public async Task RevokingTwiceGivesNotFoundTheSecondTime()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        using var first = await Invitations.RevokeAsync(client, admin.Token, created.Id);

        using var second = await Invitations.RevokeAsync(client, admin.Token, created.Id);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    // Revoking is about links; a person who already joined is removed with DELETE /members.
    [Fact]
    public async Task AUsedInvitationIsNotRevokedAndThePersonWhoJoinedStays()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        var email = TestAccounts.NewEmail();
        using (await Invitations.AcceptAsync(client, created.Token, email))
        {
        }

        using var revoked = await Invitations.RevokeAsync(client, admin.Token, created.Id);

        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        Assert.NotNull(await Invitations.ReadAsync(database, created.Id));
        var user = (await Invitations.UserByEmailAsync(database, email))!;
        Assert.NotNull(await Invitations.MembershipAsync(database, user.Id, admin.TenantId));
    }

    [Fact]
    public async Task AMemberWhoIsNotAnAdminCannotRevoke()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Member);
        var created = await Invitations.CreatedAsync(client, admin.Token);

        using var revoked = await Invitations.RevokeAsync(client, member.Token, created.Id);

        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        using var preview = await Invitations.PreviewAsync(client, created.Token);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
    }

    [Fact]
    public async Task RevokingNeedsASession()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);

        using var revoked = await Invitations.RevokeAsync(client, token: null, created.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.NotNull(await Invitations.ReadAsync(database, created.Id));
    }
}
