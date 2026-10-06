using System.Net;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Identity;

[Collection(DatabaseCollection.Name)]
public class RegisterTests(PostgresFixture database)
{
    [Fact]
    public async Task RegisterReturnsCreatedWithToken()
    {
        using var client = database.Api.CreateClient();

        var response = await TestAccounts.RegisterAsync(client, TestAccounts.NewEmail());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<AccessToken>();
        Assert.False(string.IsNullOrEmpty(token!.Token));
    }

    [Fact]
    public async Task RegisterCreatesTenantUserAndAdminMembership()
    {
        using var client = database.Api.CreateClient();
        var email = TestAccounts.NewEmail();
        var companyName = TestAccounts.NewCompanyName();

        await TestAccounts.RegisterAsync(client, email, companyName);

        // The owner context has no tenant, so identity filters would hide every row.
        await using var db = database.CreateOwnerDbContext();
        var membership = await db.Memberships
            .IgnoreQueryFilters()
            .Include(m => m.User)
            .Include(m => m.Tenant)
            .SingleAsync(m => m.User.NormalizedEmail == User.NormalizeEmail(email));

        Assert.Equal(email, membership.User.Email);
        Assert.Equal(companyName, membership.Tenant.Name);
        Assert.Equal(MembershipRole.Admin, membership.Role);
    }

    [Fact]
    public async Task RegisterStoresPasswordHashedNotAsPlainText()
    {
        using var client = database.Api.CreateClient();
        var email = TestAccounts.NewEmail();

        await TestAccounts.RegisterAsync(client, email);

        await using var db = database.CreateOwnerDbContext();
        var user = await db.Users
            .IgnoreQueryFilters()
            .SingleAsync(u => u.NormalizedEmail == User.NormalizeEmail(email));

        Assert.NotEmpty(user.PasswordHash);
        Assert.DoesNotContain(TestAccounts.Password, user.PasswordHash);
    }

    [Fact]
    public async Task RegisterTokenIdentifiesTheNewUserAndTenant()
    {
        using var client = database.Api.CreateClient();
        var email = TestAccounts.NewEmail();
        var companyName = TestAccounts.NewCompanyName();

        var registerResponse = await TestAccounts.RegisterAsync(client, email, companyName);
        var token = await TestAccounts.ReadTokenAsync(registerResponse);

        var meResponse = await TestAccounts.GetCurrentUserAsync(client, token);

        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var me = await meResponse.Content.ReadFromJsonAsync<CurrentUserResponse>();

        await using var db = database.CreateOwnerDbContext();
        var tenant = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Name == companyName);

        Assert.Equal(email, me!.Email);
        Assert.Equal(tenant.Id, me.TenantId);
        Assert.Equal(nameof(MembershipRole.Admin), me.Role);
    }

    [Fact]
    public async Task RegisterWithExistingEmailReturnsConflict()
    {
        using var client = database.Api.CreateClient();
        var email = TestAccounts.NewEmail();
        await TestAccounts.RegisterAsync(client, email);

        var response = await TestAccounts.RegisterAsync(client, email);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // decisions #12: an email is unique across the whole system, however it is typed.
    [Fact]
    public async Task RegisterTreatsEmailCaseAndSurroundingSpacesAsTheSameAddress()
    {
        using var client = database.Api.CreateClient();
        var email = TestAccounts.NewEmail();
        await TestAccounts.RegisterAsync(client, email);

        var response = await TestAccounts.RegisterAsync(client, $"  {email.ToUpperInvariant()} ");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // decisions #11: tenant, user and membership are written together or not at all.
    // Simultaneous requests get past the "is this email taken?" check together, so the
    // losers are stopped by the unique index in the middle of their insert. They must
    // answer 409 and must not leave their tenant behind.
    [Fact]
    public async Task SimultaneousRegistrationsWithTheSameEmailCreateExactlyOneTenant()
    {
        using var client = database.Api.CreateClient();
        var email = TestAccounts.NewEmail();
        var companyNames = Enumerable.Range(0, 8).Select(_ => TestAccounts.NewCompanyName()).ToList();

        var responses = await Task.WhenAll(
            companyNames.Select(companyName => TestAccounts.RegisterAsync(client, email, companyName)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(companyNames.Count - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        await using var db = database.CreateOwnerDbContext();
        var tenantsCreated = await db.Tenants.IgnoreQueryFilters().CountAsync(t => companyNames.Contains(t.Name));

        Assert.Equal(1, tenantsCreated);
    }

    [Theory]
    [InlineData("", "empty-company@example.com", "Gizli123!")]
    [InlineData("Test Firma", "not-an-email", "Gizli123!")]
    [InlineData("Test Firma", "short-password@example.com", "short")]
    public async Task RegisterWithInvalidInputReturnsBadRequest(string companyName, string email, string password)
    {
        using var client = database.Api.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/register", new { companyName, email, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
