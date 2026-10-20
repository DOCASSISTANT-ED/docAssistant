using System.Net.Http.Json;
using DocAssistant.Api.Modules.Email;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Tenants;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocAssistant.IntegrationTests.Identity;

// A copy of the API for the tests of e-mail verification, admin registration and the
// sign-in check (docs/decisions.md #70): the e-mail server is a FakeEmailSender the test
// reads codes from; everything else is the real app on the real test database.
//
// Time is not faked: the verification's timestamps are moved into the past in the
// database instead (AgeVerificationAsync). Moving the app's clock would also move the
// tokens it issues, which the token check (on the real clock) would then refuse.
internal sealed class VerificationTestApi : IAsyncDisposable
{
    public const string Password = "Gizli123!";

    private readonly WebApplicationFactory<Program> _api;
    private readonly PostgresFixture _database;

    private VerificationTestApi(PostgresFixture database, bool openRegistration)
    {
        _database = database;
        _api = database.Api.WithWebHostBuilder(builder =>
        {
            // Off as in production (decisions #70); on, as in every other test, by default.
            builder.UseSetting("Auth:OpenRegistration", openRegistration ? "true" : "false");
            builder.ConfigureTestServices(services => services.AddSingleton<IEmailSender>(Email));
        });
        Client = _api.CreateClient();
    }

    public HttpClient Client { get; }

    public FakeEmailSender Email { get; } = new();

    public static VerificationTestApi Start(PostgresFixture database, bool openRegistration = true) =>
        new(database, openRegistration);

    // ---- Companies (written straight to the database, as the platform admin would) ----

    // A company with a registration code and the given email rule; returns its id and code.
    public async Task<(Guid TenantId, string Code)> CompanyAsync(params string[] emailDomains)
    {
        var code = RegistrationCode.New();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = TestAccounts.NewCompanyName(),
            EmailDomains = [.. emailDomains],
            RegistrationCodeHash = RegistrationCode.Hash(code),
        };

        await using var db = _database.CreateOwnerDbContext();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        return (tenant.Id, code);
    }

    // As if the platform admin renewed the code: the old one stops working.
    public async Task<string> RenewCodeAsync(Guid tenantId)
    {
        var code = RegistrationCode.New();

        await using var db = _database.CreateOwnerDbContext();
        await db.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RegistrationCodeHash, RegistrationCode.Hash(code)));

        return code;
    }

    // ---- Requests ----

    public Task<HttpResponseMessage> RegisterAdminAsync(string registrationCode, string email, string password = Password) =>
        Client.PostAsJsonAsync("/auth/register-admin", new { registrationCode, email, password });

    public Task<HttpResponseMessage> RegisterAdminAsync(object body) =>
        Client.PostAsJsonAsync("/auth/register-admin", body);

    public Task<HttpResponseMessage> VerifyAsync(string email, string code) =>
        Client.PostAsJsonAsync("/auth/verify-email", new { email, code });

    public Task<HttpResponseMessage> ResendAsync(string email) =>
        Client.PostAsJsonAsync("/auth/verify-email/resend", new { email });

    public Task<HttpResponseMessage> LoginAsync(string email, string password = Password) =>
        Client.PostAsJsonAsync("/auth/login", new { email, password });

    // An admin of a new company who registered with its code and has not typed the
    // verification code yet; the e-mail with the code has been sent.
    public async Task<string> UnverifiedAdminAsync()
    {
        var (_, code) = await CompanyAsync();
        var email = TestAccounts.NewEmail();

        using var response = await RegisterAdminAsync(code, email);
        response.EnsureSuccessStatusCode();

        return email;
    }

    // ---- Looking at the database ----

    public async Task<User?> UserAsync(string email)
    {
        await using var db = _database.CreateOwnerDbContext();

        return await db.Users.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(u => u.NormalizedEmail == User.NormalizeEmail(email));
    }

    public async Task<EmailVerification?> VerificationAsync(string email)
    {
        await using var db = _database.CreateOwnerDbContext();

        return await db.EmailVerifications.AsNoTracking()
            .Where(v => db.Users.IgnoreQueryFilters().Any(u => u.Id == v.UserId && u.NormalizedEmail == User.NormalizeEmail(email)))
            .SingleOrDefaultAsync();
    }

    public async Task<Membership?> MembershipAsync(string email)
    {
        await using var db = _database.CreateOwnerDbContext();

        return await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(m => m.User.NormalizedEmail == User.NormalizeEmail(email));
    }

    // Moves the verification's clock back, as if `by` had passed: the code was sent, its
    // hour began and it expires that much earlier.
    public async Task AgeVerificationAsync(string email, TimeSpan by)
    {
        await using var db = _database.CreateOwnerDbContext();

        var updated = await db.EmailVerifications
            .Where(v => db.Users.IgnoreQueryFilters().Any(u => u.Id == v.UserId && u.NormalizedEmail == User.NormalizeEmail(email)))
            .ExecuteUpdateAsync(set => set
                .SetProperty(v => v.SentAt, v => v.SentAt - by)
                .SetProperty(v => v.WindowStartedAt, v => v.WindowStartedAt - by)
                .SetProperty(v => v.ExpiresAt, v => v.ExpiresAt - by));

        Assert.Equal(1, updated);
    }

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        return _api.DisposeAsync();
    }
}
