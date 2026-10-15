using System.Net;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace DocAssistant.IntegrationTests.Identity;

// The membership check on every request (docs/decisions.md #67). A token says who, in
// which organization and with which role, and stays valid for 60 minutes (#60); the
// database must still agree on every request. So removing someone, or changing their
// role, takes effect at once instead of when their token runs out.
[Collection(DatabaseCollection.Name)]
public class MembershipCheckTests(PostgresFixture database)
{
    // ---- Removed from the organization ----

    [Theory]
    [InlineData("/auth/me")]
    [InlineData("/documents")]
    [InlineData("/conversations")]
    public async Task ARemovedPersonsTokenIsRefusedOnTheNextRequest(string path)
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Member);
        using (var before = await Invitations.SendAsync(client, HttpMethod.Get, path, member.Token, content: null))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using (await Invitations.RemoveAsync(client, admin.Token, member.UserId))
        {
        }

        using var after = await Invitations.SendAsync(client, HttpMethod.Get, path, member.Token, content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    // The web app sends the token in the session cookie (#60); it is checked the same way.
    [Fact]
    public async Task ARemovedPersonsSessionCookieIsRefusedToo()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Member);
        using (await Invitations.RemoveAsync(client, admin.Token, member.UserId))
        {
        }

        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookie.Name}={member.Token}");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Removed is removed: signing in again does not bring the person back in.
    [Fact]
    public async Task ARemovedPersonCannotSignInToTheOrganizationAgain()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        var email = TestAccounts.NewEmail();
        using (await Invitations.AcceptAsync(client, created.Token, email))
        {
        }
        var user = (await Invitations.UserByEmailAsync(database, email))!;
        using (await Invitations.RemoveAsync(client, admin.Token, user.Id))
        {
        }

        using var login = await TestAccounts.LoginAsync(client, email);

        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    // A refused token makes the request anonymous; pages that need no sign-in still open.
    [Fact]
    public async Task ARemovedPersonsTokenDoesNotBlockAnInvitationLink()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Member);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        using (await Invitations.RemoveAsync(client, admin.Token, member.UserId))
        {
        }

        using var preview = await Invitations.SendAsync(
            client, HttpMethod.Get, $"/invitations/{created.Token}", member.Token, content: null);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
    }

    // ---- A role that changed ----

    // The token still says Admin; the database says Member. The database wins everywhere,
    // not only where being an admin matters.
    [Theory]
    [InlineData("/members")]
    [InlineData("/auth/me")]
    public async Task AnAdminTokenIsRefusedOnceThePersonIsNoLongerAnAdmin(string path)
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var other = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Admin);

        await SetRoleAsync(other.UserId, admin.TenantId, MembershipRole.Member);

        using var response = await Invitations.SendAsync(client, HttpMethod.Get, path, other.Token, content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // After signing in again the person has a token with their role as it is now.
    [Fact]
    public async Task SigningInAgainGivesATokenWithTheRoleAsItIsNow()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token, "Admin");
        var email = TestAccounts.NewEmail();
        using (await Invitations.AcceptAsync(client, created.Token, email))
        {
        }
        var user = (await Invitations.UserByEmailAsync(database, email))!;
        await SetRoleAsync(user.Id, admin.TenantId, MembershipRole.Member);

        using var login = await TestAccounts.LoginAsync(client, email);
        var token = await TestAccounts.ReadTokenAsync(login);

        using var me = await TestAccounts.GetCurrentUserAsync(client, token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("Member", (await me.Content.ReadFromJsonAsync<CurrentUserResponse>())!.Role);
        using var members = await Invitations.MembersAsync(client, token);
        Assert.Equal(HttpStatusCode.Forbidden, members.StatusCode);
    }

    // The other way round too: a member made admin needs a new token to act as one.
    [Fact]
    public async Task AMemberTokenIsRefusedOnceThePersonIsAnAdmin()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Member);

        await SetRoleAsync(member.UserId, admin.TenantId, MembershipRole.Admin);

        using var response = await TestAccounts.GetCurrentUserAsync(client, member.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Tokens for an organization the person is not in ----

    // A token the API itself signed, for a real person, but for another organization: the
    // signature is fine, the membership is not there.
    [Fact]
    public async Task ATokenForAnOrganizationThePersonIsNotInIsRefused()
    {
        using var client = database.Api.CreateClient();
        var ofA = await Invitations.RegisterAdminAsync(client);
        var ofB = await Invitations.RegisterAdminAsync(client);
        var token = database.Api.Services.GetRequiredService<JwtTokenIssuer>()
            .Issue(ofA.UserId, ofA.Email, ofB.TenantId, nameof(MembershipRole.Admin))
            .Token;

        using var members = await Invitations.MembersAsync(client, token);
        using var documents = await Invitations.SendAsync(client, HttpMethod.Get, "/documents", token, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, members.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, documents.StatusCode);
    }

    // And while nothing changes, a token keeps working.
    [Fact]
    public async Task ATokenWhoseMembershipIsUnchangedKeepsWorking()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Member);

        for (var i = 0; i < 3; i++)
        {
            using var response = await TestAccounts.GetCurrentUserAsync(client, member.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // There is no endpoint for changing a role yet; the test changes it in the database.
    private async Task SetRoleAsync(Guid userId, Guid tenantId, MembershipRole role)
    {
        await using var db = database.CreateOwnerDbContext();

        var updated = await db.Memberships.IgnoreQueryFilters()
            .Where(m => m.UserId == userId && m.TenantId == tenantId)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.Role, role));

        Assert.Equal(1, updated);
    }
}
