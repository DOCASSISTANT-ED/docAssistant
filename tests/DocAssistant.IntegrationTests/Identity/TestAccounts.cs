using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace DocAssistant.IntegrationTests.Identity;

// Shared helpers for auth tests. The test database is shared and never reset
// (docs/testing.md), so every test registers its own account with a unique email.
internal static class TestAccounts
{
    public const string Password = "Gizli123!";

    public static string NewEmail() => $"{Guid.NewGuid():N}@example.com";

    public static string NewCompanyName() => $"Test Firma {Guid.NewGuid():N}";

    public static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client,
        string email,
        string? companyName = null,
        string password = Password)
    {
        return client.PostAsJsonAsync("/auth/register", new
        {
            companyName = companyName ?? NewCompanyName(),
            email,
            password,
        });
    }

    public static Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string email,
        string password = Password)
    {
        return client.PostAsJsonAsync("/auth/login", new { email, password });
    }

    // GET /auth/me; without a token when token is null.
    public static Task<HttpResponseMessage> GetCurrentUserAsync(HttpClient client, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client.SendAsync(request);
    }

    public static async Task<string> ReadTokenAsync(HttpResponseMessage response)
    {
        var accessToken = await response.Content.ReadFromJsonAsync<AccessToken>();
        return accessToken!.Token;
    }

    // Registers a new organization (with an admin of its own) and returns its id.
    public static async Task<Guid> RegisterCompanyAsync(HttpClient client)
    {
        var registered = await RegisterAsync(client, NewEmail());
        registered.EnsureSuccessStatusCode();

        var me = await (await GetCurrentUserAsync(client, await ReadTokenAsync(registered)))
            .Content.ReadFromJsonAsync<CurrentUserResponse>();

        return me!.TenantId!.Value;
    }

    // A person in tenantId with the given role, written straight to the database (as
    // accepting an invitation would, decisions #67), and a token the API would issue for
    // them. The API checks on every request that the membership in a token still exists
    // (MembershipCheck), so a token needs a real member behind it.
    public static async Task<TestMember> AddMemberAsync(PostgresFixture database, Guid tenantId, MembershipRole role)
    {
        var email = NewEmail();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = User.NormalizeEmail(email),
            PasswordHash = "not used: this person never signs in with a password",
        };

        await using (var db = database.CreateOwnerDbContext(tenantId))
        {
            db.Users.Add(user);
            db.Memberships.Add(new Membership { Id = Guid.NewGuid(), User = user, TenantId = tenantId, Role = role });
            await db.SaveChangesAsync();
        }

        var token = database.Api.Services.GetRequiredService<JwtTokenIssuer>()
            .Issue(user.Id, email, tenantId, role.ToString())
            .Token;

        return new TestMember(token, user.Id, email, tenantId);
    }
}

internal sealed record TestMember(string Token, Guid UserId, string Email, Guid TenantId);
