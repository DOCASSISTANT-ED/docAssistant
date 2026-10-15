using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Identity;

// Helpers for the invitation and member tests (docs/decisions.md #67): the calls an admin
// and an invited person make, and the stored truth behind them.
internal static class Invitations
{
    public const string Password = TestAccounts.Password;

    // Registers a new organization; its first user is its only admin (decisions #12).
    public static async Task<TestMember> RegisterAdminAsync(HttpClient client)
    {
        var email = TestAccounts.NewEmail();
        var registered = await TestAccounts.RegisterAsync(client, email);
        registered.EnsureSuccessStatusCode();
        var token = await TestAccounts.ReadTokenAsync(registered);

        var me = await (await TestAccounts.GetCurrentUserAsync(client, token))
            .Content.ReadFromJsonAsync<CurrentUserResponse>();

        return new TestMember(token, Guid.Parse(me!.UserId!), email, me.TenantId!.Value);
    }

    // ---- Invitations ----

    // POST /invitations; the body carries a role only when one is given.
    public static Task<HttpResponseMessage> CreateAsync(HttpClient client, string? token, string? role = null) =>
        SendAsync(client, HttpMethod.Post, "/invitations", token, JsonContent.Create(role is null ? new { } : (object)new { role }));

    public static async Task<InvitationCreatedResponse> CreatedAsync(HttpClient client, string token, string? role = null)
    {
        using var response = await CreateAsync(client, token, role);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<InvitationCreatedResponse>())!;
    }

    // GET /invitations/{key}: what anyone with the link sees, without signing in.
    public static Task<HttpResponseMessage> PreviewAsync(HttpClient client, string key) =>
        SendAsync(client, HttpMethod.Get, $"/invitations/{key}", token: null, content: null);

    // POST /invitations/{key}/accept: joining with the link, without signing in.
    public static Task<HttpResponseMessage> AcceptAsync(HttpClient client, string key, string email, string password = Password) =>
        SendAsync(client, HttpMethod.Post, $"/invitations/{key}/accept", token: null, JsonContent.Create(new { email, password }));

    public static Task<HttpResponseMessage> AcceptAsync(HttpClient client, string key, object body) =>
        SendAsync(client, HttpMethod.Post, $"/invitations/{key}/accept", token: null, JsonContent.Create(body));

    public static Task<HttpResponseMessage> ListAsync(HttpClient client, string? token) =>
        SendAsync(client, HttpMethod.Get, "/invitations", token, content: null);

    public static async Task<List<InvitationResponse>> ListedAsync(HttpClient client, string token)
    {
        using var response = await ListAsync(client, token);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<List<InvitationResponse>>())!;
    }

    public static Task<HttpResponseMessage> RevokeAsync(HttpClient client, string? token, Guid invitationId) =>
        SendAsync(client, HttpMethod.Delete, $"/invitations/{invitationId}", token, content: null);

    // ---- Members ----

    public static Task<HttpResponseMessage> MembersAsync(HttpClient client, string? token) =>
        SendAsync(client, HttpMethod.Get, "/members", token, content: null);

    public static async Task<List<MemberResponse>> MemberListAsync(HttpClient client, string token)
    {
        using var response = await MembersAsync(client, token);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<List<MemberResponse>>())!;
    }

    public static Task<HttpResponseMessage> RemoveAsync(HttpClient client, string? token, Guid userId) =>
        SendAsync(client, HttpMethod.Delete, $"/members/{userId}", token, content: null);

    // ---- The stored truth, read and changed as the superuser with filters off ----

    public static async Task<Invitation?> ReadAsync(PostgresFixture database, Guid invitationId)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Invitations.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(i => i.Id == invitationId);
    }

    // As if the invitation's seven days had passed.
    public static async Task ExpireAsync(PostgresFixture database, Guid invitationId)
    {
        await using var db = database.CreateOwnerDbContext();

        var updated = await db.Invitations.IgnoreQueryFilters()
            .Where(i => i.Id == invitationId)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Equal(1, updated);
    }

    public static async Task<Membership?> MembershipAsync(PostgresFixture database, Guid userId, Guid tenantId)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId);
    }

    public static async Task<User?> UserByEmailAsync(PostgresFixture database, string email)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Users.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(u => u.NormalizedEmail == User.NormalizeEmail(email));
    }

    public static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? token, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client.SendAsync(request);
    }
}
