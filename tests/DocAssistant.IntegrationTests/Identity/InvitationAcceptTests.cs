using System.Net;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace DocAssistant.IntegrationTests.Identity;

// POST /invitations/{key}/accept (docs/decisions.md #67): whoever has a link joins the
// organization with an email and a password. The user and the membership are made in one
// transaction with the link being used up, with the role of the invitation, and the person
// comes back signed in (#60). A link works once; an email that is already registered
// cannot join.
[Collection(DatabaseCollection.Name)]
public class InvitationAcceptTests(PostgresFixture database)
{
    // ---- Joining ----

    [Theory]
    [InlineData(null, MembershipRole.Member)]
    [InlineData("Admin", MembershipRole.Admin)]
    public async Task JoiningMakesAUserInTheOrganizationWithTheRoleOfTheInvitation(string? role, MembershipRole expected)
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token, role);
        var email = TestAccounts.NewEmail();

        using var response = await Invitations.AcceptAsync(client, created.Token, email);

        // decisions #70: joining answers 202 and e-mails a code; the code signs in.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var user = (await Invitations.UserByEmailAsync(database, email))!;
        var membership = (await Invitations.MembershipAsync(database, user.Id, admin.TenantId))!;
        Assert.Equal(expected, membership.Role);
    }

    // The role is the invitation's; the person joining has no say in it.
    [Fact]
    public async Task ARoleSentWithTheRequestIsIgnored()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        var email = TestAccounts.NewEmail();

        using var response = await Invitations.AcceptAsync(
            client, created.Token, new { email, password = Invitations.Password, role = "Admin" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var user = (await Invitations.UserByEmailAsync(database, email))!;
        Assert.Equal(MembershipRole.Member, (await Invitations.MembershipAsync(database, user.Id, admin.TenantId))!.Role);
    }

    // The same as registering (#60): a token in the answer and the session cookie.
    // Superseded by decisions #70: joining no longer signs in; the e-mailed code does. To be
    // rewritten for that rule by the invitation tests' author.
    [Fact(Skip = "Superseded by docs/decisions.md #70: joining e-mails a verification code instead of signing in.")]
    public async Task ThePersonWhoJoinedIsSignedIn()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        var email = TestAccounts.NewEmail();

        using var response = await Invitations.AcceptAsync(client, created.Token, email);

        var token = await TestAccounts.ReadTokenAsync(response);
        using var me = await TestAccounts.GetCurrentUserAsync(client, token);
        var current = (await me.Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        Assert.Equal(admin.TenantId, current.TenantId);
        Assert.Equal("Member", current.Role);
        Assert.Equal(email, current.Email);

        var cookie = Assert.Single(response.Headers.GetValues(HeaderNames.SetCookie), c => c.StartsWith(SessionCookie.Name + "="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    // Afterwards the person signs in like anyone else, with the password they chose.
    [Fact]
    public async Task ThePersonWhoJoinedCanSignInAgainWithTheirPassword()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        var email = TestAccounts.NewEmail();
        using (await Invitations.AcceptAsync(client, created.Token, email, "Kendi-Parolam-42"))
        {
        }
        await Invitations.MarkVerifiedAsync(database, email);

        using var login = await TestAccounts.LoginAsync(client, email, "Kendi-Parolam-42");

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task TheLinkIsUsedUpByThePersonWhoJoined()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        var email = TestAccounts.NewEmail();

        using (await Invitations.AcceptAsync(client, created.Token, email))
        {
        }

        var user = (await Invitations.UserByEmailAsync(database, email))!;
        var stored = (await Invitations.ReadAsync(database, created.Id))!;
        Assert.NotNull(stored.AcceptedAt);
        Assert.Equal(user.Id, stored.AcceptedByUserId);
    }

    // ---- A link works once ----

    [Fact]
    public async Task ALinkCannotBeUsedTwice()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        using (await Invitations.AcceptAsync(client, created.Token, TestAccounts.NewEmail()))
        {
        }
        var second = TestAccounts.NewEmail();

        using var response = await Invitations.AcceptAsync(client, created.Token, second);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(await Invitations.UserByEmailAsync(database, second));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("unknown")]
    public async Task ALinkThatCannotBeUsedMakesNoUser(string what)
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        var key = created.Token;

        switch (what)
        {
            case "expired":
                await Invitations.ExpireAsync(database, created.Id);
                break;
            case "revoked":
                using (await Invitations.RevokeAsync(client, admin.Token, created.Id))
                {
                }
                break;
            default:
                key = "bilinmeyen-anahtar";
                break;
        }

        var email = TestAccounts.NewEmail();
        using var response = await Invitations.AcceptAsync(client, key, email);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(await Invitations.UserByEmailAsync(database, email));
    }

    // ---- Emails that are already registered ----

    // One organization per person for now (#13): an email of another organization cannot join.
    [Fact]
    public async Task AnEmailThatIsAlreadyRegisteredIsRefused()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var elsewhere = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);

        using var response = await Invitations.AcceptAsync(client, created.Token, elsewhere.Email);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(await Invitations.MembershipAsync(database, elsewhere.UserId, admin.TenantId));
    }

    // Emails are compared as the login compares them: case and spaces do not make another one.
    [Theory]
    [InlineData("upper")]
    [InlineData("spaces")]
    public async Task TheSameEmailWrittenDifferentlyIsRefusedToo(string how)
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var elsewhere = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        var written = how == "upper" ? elsewhere.Email.ToUpperInvariant() : $"  {elsewhere.Email}  ";

        using var response = await Invitations.AcceptAsync(client, created.Token, written);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // A refused join leaves the link as it was, for the right person to use.
    [Fact]
    public async Task ARefusedEmailDoesNotUseUpTheLink()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var elsewhere = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        using (await Invitations.AcceptAsync(client, created.Token, elsewhere.Email))
        {
        }

        Assert.Null((await Invitations.ReadAsync(database, created.Id))!.AcceptedAt);
        using var response = await Invitations.AcceptAsync(client, created.Token, TestAccounts.NewEmail());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // The person was removed from the organization; their account stays (#67), so the
    // email is still registered and an invitation cannot bring them back. The decision
    // allows this; it is written down here so that the consequence is seen.
    // Superseded by the note on #67 in docs/decisions.md: an account without an organization
    // joins again with its own password. To be rewritten for that rule by the invitation
    // tests' author.
    [Fact(Skip = "Superseded by docs/decisions.md #67 (rejoining): an account without an organization joins again with its own password.")]
    public async Task APersonRemovedFromTheOrganizationCannotJoinAgainWithTheSameEmail()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var first = await Invitations.CreatedAsync(client, admin.Token);
        var email = TestAccounts.NewEmail();
        using (await Invitations.AcceptAsync(client, first.Token, email))
        {
        }
        var user = (await Invitations.UserByEmailAsync(database, email))!;
        using (var removed = await Invitations.RemoveAsync(client, admin.Token, user.Id))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }
        var second = await Invitations.CreatedAsync(client, admin.Token);

        using var response = await Invitations.AcceptAsync(client, second.Token, email);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---- What the person sends ----

    [Theory]
    [InlineData("eposta-degil", "Gizli123!")]
    [InlineData("", "Gizli123!")]
    [InlineData("kisi@example.com", "kisa")]
    [InlineData("kisi@example.com", "")]
    public async Task AnInvalidEmailOrPasswordIsRefusedAndTheLinkIsNotUsedUp(string email, string password)
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);

        using var response = await Invitations.AcceptAsync(client, created.Token, email, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await Invitations.ReadAsync(database, created.Id))!.AcceptedAt);
    }

    // The password is stored as registration stores it: hashed, never as it was typed.
    [Fact]
    public async Task ThePasswordIsNotStoredAsItWasTyped()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        var email = TestAccounts.NewEmail();

        using (await Invitations.AcceptAsync(client, created.Token, email, "Kendi-Parolam-42"))
        {
        }

        var user = (await Invitations.UserByEmailAsync(database, email))!;
        Assert.False(string.IsNullOrEmpty(user.PasswordHash));
        Assert.DoesNotContain("Kendi-Parolam-42", user.PasswordHash);
    }

    // One organization per person (#13): joining makes exactly one membership.
    [Fact]
    public async Task ThePersonWhoJoinedBelongsToThisOrganizationOnly()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        var email = TestAccounts.NewEmail();

        using (await Invitations.AcceptAsync(client, created.Token, email))
        {
        }

        var user = (await Invitations.UserByEmailAsync(database, email))!;
        await using var db = database.CreateOwnerDbContext();
        var tenants = await db.Memberships.IgnoreQueryFilters().Where(m => m.UserId == user.Id).Select(m => m.TenantId).ToListAsync();
        Assert.Equal([admin.TenantId], tenants);
    }
}
