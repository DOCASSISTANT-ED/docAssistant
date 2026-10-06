using System.Net;
using System.Net.Http.Json;
using System.Text;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DocAssistant.IntegrationTests.Identity;

// GET /auth/me and, through it, token validation. Tenant isolation trusts the tenant_id
// inside the token, so a token the API did not issue must never be accepted.
[Collection(DatabaseCollection.Name)]
public class CurrentUserTests(PostgresFixture database)
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public async Task WithoutTokenReturnsUnauthorized()
    {
        using var client = database.Api.CreateClient();

        var response = await TestAccounts.GetCurrentUserAsync(client, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithValidTokenReturnsTheClaimsInTheToken()
    {
        using var client = database.Api.CreateClient();
        var token = ApiIssuer().Issue(UserId, "ayse@example.com", TenantId, "Member").Token;

        var response = await TestAccounts.GetCurrentUserAsync(client, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.Equal(UserId.ToString(), me!.UserId);
        Assert.Equal("ayse@example.com", me.Email);
        Assert.Equal(TenantId, me.TenantId);
        Assert.Equal("Member", me.Role);
    }

    [Fact]
    public async Task TokenSignedWithAnotherKeyIsRejected()
    {
        using var client = database.Api.CreateClient();
        var attackerIssuer = IssuerWith(signingKey: "an-attacker-does-not-know-the-real-signing-key-0123456789");
        var token = attackerIssuer.Issue(UserId, "ayse@example.com", TenantId, "Admin").Token;

        var response = await TestAccounts.GetCurrentUserAsync(client, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredTokenIsRejected()
    {
        using var client = database.Api.CreateClient();
        // Issued two hours ago with the real key; access tokens live for one hour.
        var twoHoursAgo = new FixedTimeProvider(DateTimeOffset.UtcNow.AddHours(-2));
        var token = IssuerWith(timeProvider: twoHoursAgo).Issue(UserId, "ayse@example.com", TenantId, "Admin").Token;

        var response = await TestAccounts.GetCurrentUserAsync(client, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // A token is header.payload.signature. Changing the tenant in the payload without
    // being able to re-sign it must invalidate the token.
    [Fact]
    public async Task TokenWithATamperedTenantIsRejected()
    {
        using var client = database.Api.CreateClient();
        var otherTenantId = Guid.NewGuid();
        var token = ApiIssuer().Issue(UserId, "ayse@example.com", TenantId, "Admin").Token;

        var parts = token.Split('.');
        var payload = Base64UrlEncoder.Decode(parts[1]);
        Assert.Contains(TenantId.ToString(), payload);
        var tamperedPayload = payload.Replace(TenantId.ToString(), otherTenantId.ToString());
        var tamperedToken = $"{parts[0]}.{Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(tamperedPayload))}.{parts[2]}";

        var response = await TestAccounts.GetCurrentUserAsync(client, tamperedToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The issuer the API itself uses.
    private JwtTokenIssuer ApiIssuer() => database.Api.Services.GetRequiredService<JwtTokenIssuer>();

    // An issuer like the API's, except for the given signing key or clock.
    private JwtTokenIssuer IssuerWith(string? signingKey = null, TimeProvider? timeProvider = null)
    {
        var apiOptions = database.Api.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

        var options = new JwtOptions
        {
            Issuer = apiOptions.Issuer,
            Audience = apiOptions.Audience,
            SigningKey = signingKey ?? apiOptions.SigningKey,
            AccessTokenMinutes = apiOptions.AccessTokenMinutes,
        };

        return new JwtTokenIssuer(Options.Create(options), timeProvider ?? TimeProvider.System);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
