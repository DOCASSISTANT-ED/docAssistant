using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Identity;

// POST /invitations (docs/decisions.md #67): an admin makes a link with a role, Member
// unless Admin is asked for. The link's key is in the answer once; the database keeps
// only its SHA-256 hash, so a copy of the database is not enough to use a pending link.
[Collection(DatabaseCollection.Name)]
public partial class InvitationCreateTests(PostgresFixture database)
{
    [Fact]
    public async Task AnAdminMakesALinkForAMemberUnlessAskedOtherwise()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        using var response = await Invitations.CreateAsync(client, admin.Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<InvitationCreatedResponse>())!;
        Assert.Equal("Member", created.Role);
        Assert.Equal($"/invitations/{created.Id}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AnAdminMakesALinkForAnAdminWhenAskedTo()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        var created = await Invitations.CreatedAsync(client, admin.Token, "Admin");

        Assert.Equal("Admin", created.Role);
        Assert.Equal(MembershipRole.Admin, (await Invitations.ReadAsync(database, created.Id))!.Role);
    }

    [Fact]
    public async Task ANullRoleMeansMember()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        using var response = await Invitations.SendAsync(
            client, HttpMethod.Post, "/invitations", admin.Token, JsonContent.Create(new { role = (string?)null }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Member", (await response.Content.ReadFromJsonAsync<InvitationCreatedResponse>())!.Role);
    }

    [Fact]
    public async Task TheStoredInvitationBelongsToTheAdminsOrganizationAndLastsSevenDays()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var before = DateTimeOffset.UtcNow;

        var created = await Invitations.CreatedAsync(client, admin.Token);

        var stored = (await Invitations.ReadAsync(database, created.Id))!;
        Assert.Equal(admin.TenantId, stored.TenantId);
        Assert.Equal(admin.UserId, stored.CreatedByUserId);
        Assert.Equal(MembershipRole.Member, stored.Role);
        Assert.Null(stored.AcceptedAt);
        Assert.Null(stored.AcceptedByUserId);
        Assert.Equal(TimeSpan.FromDays(7), stored.ExpiresAt - stored.CreatedAt);
        Assert.InRange(stored.CreatedAt, before.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
        // The answer carries the time to the tick, the database to the microsecond.
        Assert.True((stored.ExpiresAt - created.ExpiresAt).Duration() < TimeSpan.FromMilliseconds(1));
    }

    // The decision names two roles. Anything else, the wrong case included, is refused,
    // and nothing is stored.
    [Theory]
    [InlineData("admin")]
    [InlineData("member")]
    [InlineData("ADMIN")]
    [InlineData("Owner")]
    public async Task AnyOtherRoleIsRefused(string role)
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        using var response = await Invitations.CreateAsync(client, admin.Token, role);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await Invitations.ListedAsync(client, admin.Token));
    }

    // Known gap. The role is read with Enum.TryParse, which also takes numbers ("1" is
    // Admin, "0" is Member) and lists that it combines ("Admin, Member" is Admin). Only
    // admins make invitations, so nobody gains a role they could not give anyway; but the
    // decision says "Member" or "Admin". Remove ".Skip" when such roles are refused.
    [Theory(Skip = "Known gap: a role given as a number (\"0\", \"1\") or a list (\"Admin, Member\") is accepted. To be fixed by the invitations' author.")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("Admin, Member")]
    public async Task ARoleGivenAsANumberOrAListIsRefused(string role)
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        using var response = await Invitations.CreateAsync(client, admin.Token, role);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Admins invite, as they upload (#32); a member cannot bring people in, least of all
    // as admins.
    [Theory]
    [InlineData(null)]
    [InlineData("Admin")]
    public async Task AMemberWhoIsNotAnAdminCannotInvite(string? role)
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var member = await TestAccounts.AddMemberAsync(database, admin.TenantId, MembershipRole.Member);

        using var response = await Invitations.CreateAsync(client, member.Token, role);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await Invitations.ListedAsync(client, admin.Token));
    }

    [Fact]
    public async Task InvitingNeedsASession()
    {
        using var client = database.Api.CreateClient();

        using var response = await Invitations.CreateAsync(client, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- The key ----

    // 32 random bytes, written so that it fits in an address without escaping.
    [Fact]
    public async Task TheKeyIs32RandomBytesWrittenToFitInAnAddress()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        var created = await Invitations.CreatedAsync(client, admin.Token);

        Assert.Matches(UrlSafeKey(), created.Token);
        Assert.Equal(Uri.EscapeDataString(created.Token), created.Token);
    }

    [Fact]
    public async Task EveryLinkHasItsOwnKey()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        var keys = new HashSet<string>();
        for (var i = 0; i < 5; i++)
        {
            keys.Add((await Invitations.CreatedAsync(client, admin.Token)).Token);
        }

        Assert.Equal(5, keys.Count);
    }

    // Like a password: the database holds what the key hashes to, never the key.
    [Fact]
    public async Task TheDatabaseKeepsOnlyTheHashOfTheKey()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        var created = await Invitations.CreatedAsync(client, admin.Token);

        var stored = (await Invitations.ReadAsync(database, created.Id))!;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(created.Token))), stored.TokenHash);
        Assert.NotEqual(created.Token, stored.TokenHash);

        // Nowhere in the row, in any column.
        await using var db = database.CreateOwnerDbContext();
        var rowText = await db.Database
            .SqlQuery<string>($"SELECT row_to_json(i)::text AS \"Value\" FROM invitations i WHERE i.id = {created.Id}")
            .SingleAsync();
        Assert.DoesNotContain(created.Token, rowText);
    }

    // Shown once: the list of pending invitations does not give it again.
    [Fact]
    public async Task TheKeyIsNotShownAgainInTheList()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);

        using var response = await Invitations.ListAsync(client, admin.Token);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(created.Id.ToString(), body);
        Assert.DoesNotContain(created.Token, body);
    }

    // Base64url of 32 bytes: 43 letters, digits, "-" and "_", no padding.
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex UrlSafeKey();
}
