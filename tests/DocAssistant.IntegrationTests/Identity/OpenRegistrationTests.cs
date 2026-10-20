using System.Net;
using DocAssistant.IntegrationTests.Infrastructure;

namespace DocAssistant.IntegrationTests.Identity;

// Auth:OpenRegistration (docs/decisions.md #70): whether anyone may open a company with
// POST /auth/register. On in development, tests and the evaluation run; off in
// production, where the endpoint does not exist at all. Off when the setting is missing,
// so a deployment that forgets it stays closed.
[Collection(DatabaseCollection.Name)]
public class OpenRegistrationTests(PostgresFixture database)
{
    [Fact]
    public async Task WhenOnAnyoneCanOpenACompany()
    {
        await using var api = VerificationTestApi.Start(database, openRegistration: true);

        using var response = await TestAccounts.RegisterAsync(api.Client, TestAccounts.NewEmail());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task WhenOffTheEndpointDoesNotExist()
    {
        await using var api = VerificationTestApi.Start(database, openRegistration: false);
        var email = TestAccounts.NewEmail();

        using var response = await TestAccounts.RegisterAsync(api.Client, email);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(await api.UserAsync(email));
    }

    // Signing in, verifying and joining by invitation are not affected.
    [Fact]
    public async Task WhenOffTheOtherWaysInStillWork()
    {
        await using var api = VerificationTestApi.Start(database, openRegistration: false);
        var email = await api.UnverifiedAdminAsync();

        using var verify = await api.VerifyAsync(email, api.Email.LastCodeFor(email));
        using var login = await api.LoginAsync(email);

        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // The default, when the setting is missing, is off.
    [Fact]
    public void TheSettingIsOffWhenNotSet() =>
        Assert.False(new DocAssistant.Api.Modules.Identity.AuthOptions().OpenRegistration);
}
