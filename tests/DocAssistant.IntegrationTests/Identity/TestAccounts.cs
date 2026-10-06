using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Identity;

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
}
