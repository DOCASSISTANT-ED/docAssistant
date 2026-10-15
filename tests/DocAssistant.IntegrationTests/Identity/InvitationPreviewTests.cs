using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Identity;

// GET /invitations/{key} (docs/decisions.md #67): whoever opens a link sees, without
// signing in, which organization it is for and with which role, and nothing more. A link
// that cannot be used (unknown, used, revoked, expired) is "not found", the same way in
// every case, so a key cannot be probed for what happened to it.
[Collection(DatabaseCollection.Name)]
public class InvitationPreviewTests(PostgresFixture database)
{
    [Theory]
    [InlineData(null, "Member")]
    [InlineData("Admin", "Admin")]
    public async Task ALinkShowsTheOrganizationAndTheRoleWithoutSigningIn(string? role, string shown)
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token, role);

        using var response = await Invitations.PreviewAsync(client, created.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = (await response.Content.ReadFromJsonAsync<InvitationPreviewResponse>())!;
        Assert.Equal(await CompanyNameAsync(admin.TenantId), preview.CompanyName);
        Assert.Equal(shown, preview.Role);
        Assert.True((preview.ExpiresAt - created.ExpiresAt).Duration() < TimeSpan.FromMilliseconds(1));
    }

    // Someone who is not in the organization yet is told only what they need to decide:
    // no ids, no names or emails of the people in it.
    [Fact]
    public async Task ALinkShowsNothingMore()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);

        using var response = await Invitations.PreviewAsync(client, created.Token);

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            ["companyName", "expiresAt", "role"],
            json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(admin.Email, body);
        Assert.DoesNotContain(admin.TenantId.ToString(), body);
        Assert.DoesNotContain(created.Id.ToString(), body);
    }

    // Opening a link does not use it up; only joining does.
    [Fact]
    public async Task LookingAtALinkDoesNotUseItUp()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);

        using (await Invitations.PreviewAsync(client, created.Token))
        using (await Invitations.PreviewAsync(client, created.Token))
        {
        }

        Assert.Null((await Invitations.ReadAsync(database, created.Id))!.AcceptedAt);

        using var accepted = await Invitations.AcceptAsync(client, created.Token, TestAccounts.NewEmail());
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    // ---- Links that cannot be used ----

    [Fact]
    public async Task AnUnknownKeyIsNotFound()
    {
        using var client = database.Api.CreateClient();

        using var response = await Invitations.PreviewAsync(client, "bu-bir-davet-anahtari-degil");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The key is compared whole: one letter off is another key.
    [Fact]
    public async Task AKeyWithOneLetterChangedIsNotFound()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        var changed = (created.Token[0] == 'A' ? 'B' : 'A') + created.Token[1..];

        using var response = await Invitations.PreviewAsync(client, changed);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AUsedLinkIsNotFound()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        using (await Invitations.AcceptAsync(client, created.Token, TestAccounts.NewEmail()))
        {
        }

        using var response = await Invitations.PreviewAsync(client, created.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ARevokedLinkIsNotFound()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        using (await Invitations.RevokeAsync(client, admin.Token, created.Id))
        {
        }

        using var response = await Invitations.PreviewAsync(client, created.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnExpiredLinkIsNotFound()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var created = await Invitations.CreatedAsync(client, admin.Token);
        await Invitations.ExpireAsync(database, created.Id);

        using var response = await Invitations.PreviewAsync(client, created.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Unknown, used, revoked and expired give the very same answer: a key that leaked
    // tells nothing about what became of it.
    [Fact]
    public async Task EveryLinkThatCannotBeUsedLooksTheSame()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var used = await Invitations.CreatedAsync(client, admin.Token);
        var revoked = await Invitations.CreatedAsync(client, admin.Token);
        var expired = await Invitations.CreatedAsync(client, admin.Token);
        using (await Invitations.AcceptAsync(client, used.Token, TestAccounts.NewEmail()))
        using (await Invitations.RevokeAsync(client, admin.Token, revoked.Id))
        {
        }
        await Invitations.ExpireAsync(database, expired.Id);

        var answers = new List<(HttpStatusCode Status, string Body)>();
        foreach (var key in new[] { "bilinmeyen-anahtar", used.Token, revoked.Token, expired.Token })
        {
            using var response = await Invitations.PreviewAsync(client, key);
            answers.Add((response.StatusCode, await response.Content.ReadAsStringAsync()));
        }

        Assert.All(answers, answer => Assert.Equal(answers[0], answer));
        Assert.Equal(HttpStatusCode.NotFound, answers[0].Status);
    }

    private async Task<string> CompanyNameAsync(Guid tenantId)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Tenants.IgnoreQueryFilters().Where(t => t.Id == tenantId).Select(t => t.Name).SingleAsync();
    }
}
