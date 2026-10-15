using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DocAssistant.IntegrationTests.Identity;

// The session cookie (docs/decisions.md #60): login and registration set it, the API
// accepts the token from it, logout removes it, and a request that relies on it and
// changes something must carry the X-Requested-With header.
//
// The tests build the Cookie header themselves and read Set-Cookie themselves, as a
// browser would, so that nothing depends on the test client's own cookie handling.
[Collection(DatabaseCollection.Name)]
public class SessionCookieTests(PostgresFixture database)
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();

    // ---- Login and registration set the cookie ----------------------------------------

    [Fact]
    public async Task LoginSetsTheCookieWithTheSameTokenAsTheBody()
    {
        using var client = CreateClient();
        var email = TestAccounts.NewEmail();
        await TestAccounts.RegisterAsync(client, email);

        var response = await TestAccounts.LoginAsync(client, email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<AccessToken>();
        var cookie = SessionCookieOf(response);
        Assert.NotNull(cookie);
        Assert.Equal(token!.Token, cookie.Value.Value);
    }

    [Fact]
    public async Task RegistrationSetsTheCookieWithTheSameTokenAsTheBody()
    {
        using var client = CreateClient();

        var response = await TestAccounts.RegisterAsync(client, TestAccounts.NewEmail());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<AccessToken>();
        var cookie = SessionCookieOf(response);
        Assert.NotNull(cookie);
        Assert.Equal(token!.Token, cookie.Value.Value);
    }

    // What makes the cookie safe to hold a token: scripts cannot read it, other sites
    // cannot make the browser send it, and it travels over HTTPS only.
    [Fact]
    public async Task TheCookieIsHttpOnlySameSiteStrictSecureAndSiteWide()
    {
        using var client = CreateClient();

        var response = await TestAccounts.RegisterAsync(client, TestAccounts.NewEmail());

        var cookie = SessionCookieOf(response)!;
        Assert.True(cookie.HttpOnly);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.True(cookie.Secure);
        Assert.Equal("/", cookie.Path.Value);
    }

    // The cookie's date has whole seconds; the token's has fractions.
    [Fact]
    public async Task TheCookieExpiresWhenTheTokenDoes()
    {
        using var client = CreateClient();

        var response = await TestAccounts.RegisterAsync(client, TestAccounts.NewEmail());

        var token = await response.Content.ReadFromJsonAsync<AccessToken>();
        var cookie = SessionCookieOf(response)!;
        Assert.NotNull(cookie.Expires);
        Assert.InRange((token!.ExpiresAt - cookie.Expires.Value).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task FailedLoginSetsNoCookie()
    {
        using var client = CreateClient();
        var email = TestAccounts.NewEmail();
        await TestAccounts.RegisterAsync(client, email);

        var response = await TestAccounts.LoginAsync(client, email, "wrong-password");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(SessionCookieOf(response));
    }

    [Fact]
    public async Task RegistrationWithATakenEmailSetsNoCookie()
    {
        using var client = CreateClient();
        var email = TestAccounts.NewEmail();
        await TestAccounts.RegisterAsync(client, email);

        var response = await TestAccounts.RegisterAsync(client, email);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(SessionCookieOf(response));
    }

    // ---- The API accepts the token from the cookie -------------------------------------

    [Fact]
    public async Task TheCookieFromLoginSignsTheUserIn()
    {
        using var client = CreateClient();
        var email = TestAccounts.NewEmail();
        var registered = await TestAccounts.RegisterAsync(client, email);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/auth/me", cookie: SessionCookieOf(registered)!.Value.Value));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.Equal(email, me!.Email);
        Assert.Equal(nameof(MembershipRole.Admin), me.Role);
        Assert.NotNull(me.TenantId);
    }

    [Fact]
    public async Task WithoutCookieOrHeaderTheRequestIsUnauthorized()
    {
        using var client = CreateClient();

        var response = await client.SendAsync(Request(HttpMethod.Get, "/auth/me"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The cookie is only another way of sending the token; the same checks apply to it.
    [Fact]
    public async Task ACookieTokenSignedWithAnotherKeyIsRejected()
    {
        using var client = CreateClient();
        var token = IssuerWith(signingKey: "an-attacker-does-not-know-the-real-signing-key-0123456789")
            .Issue(UserId, "ayse@example.com", TenantId, "Admin").Token;

        var response = await client.SendAsync(Request(HttpMethod.Get, "/auth/me", cookie: token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnExpiredCookieTokenIsRejected()
    {
        using var client = CreateClient();
        var twoHoursAgo = new FixedTimeProvider(DateTimeOffset.UtcNow.AddHours(-2));
        var token = IssuerWith(timeProvider: twoHoursAgo).Issue(UserId, "ayse@example.com", TenantId, "Admin").Token;

        var response = await client.SendAsync(Request(HttpMethod.Get, "/auth/me", cookie: token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ACookieWithAnotherNameIsIgnored()
    {
        using var client = CreateClient();
        var request = Request(HttpMethod.Get, "/auth/me");
        request.Headers.Add(HeaderNames.Cookie, $"session={ValidToken()}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- The Authorization header comes first ------------------------------------------

    [Fact]
    public async Task TheHeaderWinsWhenBothAreSent()
    {
        using var client = CreateClient();
        var tenantId = await TestAccounts.RegisterCompanyAsync(client);
        var headerMember = await TestAccounts.AddMemberAsync(database, tenantId, MembershipRole.Member);
        var cookieMember = await TestAccounts.AddMemberAsync(database, tenantId, MembershipRole.Admin);

        var response = await client.SendAsync(
            Request(HttpMethod.Get, "/auth/me", cookie: cookieMember.Token, bearer: headerMember.Token));

        var me = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.Equal(headerMember.Email, me!.Email);
        Assert.Equal("Member", me.Role);
    }

    // With a header present the cookie is not looked at, even when the header's token is
    // no good: the request does not quietly fall back to the cookie's user.
    [Fact]
    public async Task AnInvalidHeaderIsNotRescuedByAValidCookie()
    {
        using var client = CreateClient();

        var response = await client.SendAsync(Request(HttpMethod.Get, "/auth/me", cookie: ValidToken(), bearer: "not-a-token"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Logout ------------------------------------------------------------------------

    // A browser removes a cookie when it gets it back empty, already expired, and with the
    // name and path it was set with.
    [Fact]
    public async Task LogoutTellsTheBrowserToRemoveTheCookie()
    {
        using var client = CreateClient();

        var response = await client.SendAsync(
            Request(HttpMethod.Post, "/auth/logout", cookie: ValidToken(), requestedWith: true));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = SessionCookieOf(response);
        Assert.NotNull(cookie);
        Assert.Equal(string.Empty, cookie.Value.Value);
        Assert.NotNull(cookie.Expires);
        Assert.True(cookie.Expires < DateTimeOffset.UtcNow);
        Assert.Equal("/", cookie.Path.Value);
    }

    // Signing out must not depend on still being signed in.
    [Fact]
    public async Task LogoutWorksWithoutACookie()
    {
        using var client = CreateClient();

        var response = await client.SendAsync(Request(HttpMethod.Post, "/auth/logout"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, SessionCookieOf(response)!.Value.Value);
    }

    [Fact]
    public async Task LogoutWorksWithAnExpiredCookie()
    {
        using var client = CreateClient();
        var twoHoursAgo = new FixedTimeProvider(DateTimeOffset.UtcNow.AddHours(-2));
        var token = IssuerWith(timeProvider: twoHoursAgo).Issue(UserId, "ayse@example.com", TenantId, "Admin").Token;

        var response = await client.SendAsync(
            Request(HttpMethod.Post, "/auth/logout", cookie: token, requestedWith: true));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---- CSRF: X-Requested-With --------------------------------------------------------

    // What a form on another site could send: the cookie, but no custom header.
    [Fact]
    public async Task AChangingRequestThatReliesOnTheCookieNeedsTheHeader()
    {
        using var client = CreateClient();

        var response = await client.SendAsync(Request(HttpMethod.Post, "/auth/logout", cookie: ValidToken()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(SessionCookieCsrfProtection.MissingHeaderTitle, problem!.Title);

        // The request was turned away before the endpoint: the cookie was not removed.
        Assert.Null(SessionCookieOf(response));
    }

    // The header has to say something; its name alone is not enough.
    [Fact]
    public async Task ABlankHeaderDoesNotCount()
    {
        using var client = CreateClient();
        var request = Request(HttpMethod.Post, "/auth/logout", cookie: ValidToken());
        request.Headers.TryAddWithoutValidation(SessionCookieCsrfProtection.HeaderName, "   ");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task EveryChangingMethodNeedsTheHeader(string method)
    {
        using var client = CreateClient();

        var response = await client.SendAsync(Request(new HttpMethod(method), "/auth/me", cookie: ValidToken()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Reading changes nothing, so a page load or a link does not need the header.
    [Fact]
    public async Task AReadingRequestDoesNotNeedTheHeader()
    {
        using var client = CreateClient();
        var member = await TestAccounts.AddMemberAsync(
            database, await TestAccounts.RegisterCompanyAsync(client), MembershipRole.Member);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/auth/me", cookie: member.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Clients outside the browser (tests, the evaluation run) send the token in the
    // header; a browser never adds that header by itself, so nothing can be forged.
    [Fact]
    public async Task ARequestWithAnAuthorizationHeaderDoesNotNeedTheHeader()
    {
        using var client = CreateClient();

        var response = await client.SendAsync(Request(HttpMethod.Post, "/auth/logout", bearer: ValidToken()));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ARequestWithBothHeaderAndCookieDoesNotNeedTheHeader()
    {
        using var client = CreateClient();

        var response = await client.SendAsync(
            Request(HttpMethod.Post, "/auth/logout", cookie: ValidToken(), bearer: ValidToken()));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // Login and registration are sent before there is a cookie.
    [Fact]
    public async Task ARequestWithoutACookieDoesNotNeedTheHeader()
    {
        using var client = CreateClient();

        var response = await TestAccounts.RegisterAsync(client, TestAccounts.NewEmail());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- Helpers -----------------------------------------------------------------------

    // The test client would otherwise keep cookies itself; here every request says
    // exactly what it sends.
    private HttpClient CreateClient() =>
        database.Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static HttpRequestMessage Request(
        HttpMethod method,
        string path,
        string? cookie = null,
        string? bearer = null,
        bool requestedWith = false)
    {
        var request = new HttpRequestMessage(method, path);

        if (cookie is not null)
        {
            request.Headers.Add(HeaderNames.Cookie, $"{SessionCookie.Name}={cookie}");
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (requestedWith)
        {
            // The value the Angular app sends; the API only asks that there is one.
            request.Headers.Add(SessionCookieCsrfProtection.HeaderName, "XMLHttpRequest");
        }

        return request;
    }

    // The session cookie the response sets or removes; null when it does neither.
    internal static SetCookieHeaderValue? SessionCookieOf(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(HeaderNames.SetCookie, out var values))
        {
            return null;
        }

        return SetCookieHeaderValue.ParseList(values.ToList())
            .SingleOrDefault(cookie => cookie.Name == SessionCookie.Name);
    }

    private string ValidToken() => ApiIssuer().Issue(UserId, "ayse@example.com", TenantId, "Admin").Token;

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
