using System.Net;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;

namespace DocAssistant.IntegrationTests.Identity;

// Checks the invitation test setup itself: an admin's link can be looked at and used, and
// the person who joins can make requests. When this fails, the invitation and member
// tests fail for the same reason.
[Collection(DatabaseCollection.Name)]
public class InvitationsTests(PostgresFixture database)
{
    [Fact]
    public async Task AnAdminsLinkCanBeLookedAtAndUsedAndThePersonWhoJoinedIsSignedIn()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);

        var invitation = await Invitations.CreatedAsync(client, admin.Token);
        using var preview = await Invitations.PreviewAsync(client, invitation.Token);
        var email = TestAccounts.NewEmail();
        using var accepted = await Invitations.AcceptAsync(client, invitation.Token, email);
        var token = await TestAccounts.ReadTokenAsync(accepted);
        using var me = await TestAccounts.GetCurrentUserAsync(client, token);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var current = (await me.Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        Assert.Equal(admin.TenantId, current.TenantId);
        Assert.Equal(email, current.Email);
    }

    [Fact]
    public async Task AnInvitationCanBeMadeToLookExpired()
    {
        using var client = database.Api.CreateClient();
        var admin = await Invitations.RegisterAdminAsync(client);
        var invitation = await Invitations.CreatedAsync(client, admin.Token);

        await Invitations.ExpireAsync(database, invitation.Id);

        var stored = await Invitations.ReadAsync(database, invitation.Id);
        Assert.True(stored!.ExpiresAt < DateTimeOffset.UtcNow);
    }
}
