using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;

namespace DocAssistant.IntegrationTests.Identity;

[Collection(DatabaseCollection.Name)]
public class LoginTests(PostgresFixture database)
{
    [Fact]
    public async Task LoginWithCorrectCredentialsReturnsToken()
    {
        using var client = database.Api.CreateClient();
        var email = TestAccounts.NewEmail();
        await TestAccounts.RegisterAsync(client, email);

        var response = await TestAccounts.LoginAsync(client, email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(await TestAccounts.ReadTokenAsync(response));
    }

    [Fact]
    public async Task LoginIgnoresEmailCase()
    {
        using var client = database.Api.CreateClient();
        var email = TestAccounts.NewEmail();
        await TestAccounts.RegisterAsync(client, email);

        var response = await TestAccounts.LoginAsync(client, email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LoginWithWrongPasswordReturnsUnauthorized()
    {
        using var client = database.Api.CreateClient();
        var email = TestAccounts.NewEmail();
        await TestAccounts.RegisterAsync(client, email);

        var response = await TestAccounts.LoginAsync(client, email, "wrong-password");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoginWithUnknownEmailReturnsUnauthorized()
    {
        using var client = database.Api.CreateClient();

        var response = await TestAccounts.LoginAsync(client, TestAccounts.NewEmail());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // If the two answers differed, anyone could find out which emails are registered.
    [Fact]
    public async Task WrongPasswordAndUnknownEmailAreIndistinguishable()
    {
        using var client = database.Api.CreateClient();
        var registeredEmail = TestAccounts.NewEmail();
        await TestAccounts.RegisterAsync(client, registeredEmail);

        var wrongPassword = await TestAccounts.LoginAsync(client, registeredEmail, "wrong-password");
        var unknownEmail = await TestAccounts.LoginAsync(client, TestAccounts.NewEmail());

        Assert.Equal(wrongPassword.StatusCode, unknownEmail.StatusCode);
        Assert.Equal(await ReadProblemAsync(wrongPassword), await ReadProblemAsync(unknownEmail));
    }

    // decisions #13: in phase 1 a user has one membership and signs in to that tenant.
    [Fact]
    public async Task LoginTokenBelongsToTheUserAndTenantCreatedAtRegistration()
    {
        using var client = database.Api.CreateClient();
        var email = TestAccounts.NewEmail();
        var registerResponse = await TestAccounts.RegisterAsync(client, email);
        var registered = await ReadCurrentUserAsync(client, await TestAccounts.ReadTokenAsync(registerResponse));

        var loginResponse = await TestAccounts.LoginAsync(client, email);
        var loggedIn = await ReadCurrentUserAsync(client, await TestAccounts.ReadTokenAsync(loginResponse));

        Assert.NotNull(registered.TenantId);
        Assert.Equal(registered.TenantId, loggedIn.TenantId);
        Assert.Equal(registered.UserId, loggedIn.UserId);
        Assert.Equal(nameof(MembershipRole.Admin), loggedIn.Role);
    }

    private static async Task<CurrentUserResponse> ReadCurrentUserAsync(HttpClient client, string token)
    {
        var response = await TestAccounts.GetCurrentUserAsync(client, token);
        return (await response.Content.ReadFromJsonAsync<CurrentUserResponse>())!;
    }

    // The fields a caller can see, without traceId: that one is different for every request.
    private static async Task<string> ReadProblemAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var visibleFields = body.RootElement.EnumerateObject()
            .Where(field => field.Name != "traceId")
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .Select(field => $"{field.Name}={field.Value}");

        return string.Join("; ", visibleFields);
    }
}
