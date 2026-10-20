using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.Net.Http.Headers;

namespace DocAssistant.IntegrationTests.Identity;

// POST /auth/register-admin (docs/decisions.md #70): one of a company's admins registers
// with the company's registration code, which is always required. The company is found
// from the code; its email rule applies; the account is made unverified, with an Admin
// membership, and a verification code is e-mailed. The answer does not sign in.
// Order of the checks: code, email rule, address already registered.
[Collection(DatabaseCollection.Name)]
public class AdminRegistrationTests(PostgresFixture database)
{
    // ---- Registering ----

    [Fact]
    public async Task TheCodeMakesAnUnverifiedAdminOfItsCompanyAndSendsACode()
    {
        await using var api = VerificationTestApi.Start(database);
        var (tenantId, code) = await api.CompanyAsync();
        var email = TestAccounts.NewEmail();

        using var response = await api.RegisterAdminAsync(code, email);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<AdminRegistrationResponse>())!.VerificationSent);

        var user = (await api.UserAsync(email))!;
        Assert.Null(user.EmailVerifiedAt);
        Assert.False(user.IsPlatformAdmin);

        var membership = (await api.MembershipAsync(email))!;
        Assert.Equal(tenantId, membership.TenantId);
        Assert.Equal(MembershipRole.Admin, membership.Role);

        Assert.Single(api.Email.SentTo(email));
    }

    // The person signs in only after typing the code: no token, no cookie.
    [Fact]
    public async Task RegisteringDoesNotSignIn()
    {
        await using var api = VerificationTestApi.Start(database);
        var (_, code) = await api.CompanyAsync();

        using var response = await api.RegisterAdminAsync(code, TestAccounts.NewEmail());

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["verificationSent"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.False(response.Headers.TryGetValues(HeaderNames.SetCookie, out _));
    }

    [Fact]
    public async Task ThePasswordGivenAtRegistrationIsTheOneThatSignsIn()
    {
        await using var api = VerificationTestApi.Start(database);
        var (_, code) = await api.CompanyAsync();
        var email = TestAccounts.NewEmail();
        using (await api.RegisterAdminAsync(code, email, "Kendi-Parolam-42"))
        {
        }

        using (await api.VerifyAsync(email, api.Email.LastCodeFor(email)))
        {
        }

        using var wrong = await api.LoginAsync(email, VerificationTestApi.Password);
        using var right = await api.LoginAsync(email, "Kendi-Parolam-42");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    // A company may have several admins: the code is not used up (#70).
    [Fact]
    public async Task SeveralAdminsCanRegisterWithTheSameCode()
    {
        await using var api = VerificationTestApi.Start(database);
        var (tenantId, code) = await api.CompanyAsync();
        var first = TestAccounts.NewEmail();
        var second = TestAccounts.NewEmail();

        using var one = await api.RegisterAdminAsync(code, first);
        using var two = await api.RegisterAdminAsync(code, second);

        Assert.Equal(HttpStatusCode.Accepted, one.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, two.StatusCode);
        Assert.Equal(tenantId, (await api.MembershipAsync(second))!.TenantId);
    }

    // Codes are read aloud and typed by hand: dashes, spaces and case do not count.
    [Theory]
    [InlineData("lower")]
    [InlineData("nodashes")]
    [InlineData("spaces")]
    public async Task TheCodeIsMatchedWithoutDashesSpacesOrCase(string form)
    {
        await using var api = VerificationTestApi.Start(database);
        var (_, code) = await api.CompanyAsync();
        var typed = form switch
        {
            "lower" => code.ToLowerInvariant(),
            "nodashes" => code.Replace("-", string.Empty),
            _ => " " + code.Replace("-", " ") + " ",
        };

        using var response = await api.RegisterAdminAsync(typed, TestAccounts.NewEmail());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // ---- The code ----

    [Fact]
    public async Task AnUnknownCodeIsRefused()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = TestAccounts.NewEmail();

        using var response = await api.RegisterAdminAsync("AAAA-BBBB-CCCC", email);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "The registration code is not valid.");
        Assert.Null(await api.UserAsync(email));
        Assert.Empty(api.Email.SentTo(email));
    }

    [Fact]
    public async Task ARenewedCodeStopsTheOldOneAtOnce()
    {
        await using var api = VerificationTestApi.Start(database);
        var (tenantId, oldCode) = await api.CompanyAsync();
        var newCode = await api.RenewCodeAsync(tenantId);

        using var old = await api.RegisterAdminAsync(oldCode, TestAccounts.NewEmail());
        using var current = await api.RegisterAdminAsync(newCode, TestAccounts.NewEmail());

        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, current.StatusCode);
    }

    // ---- The email rule ----

    [Theory]
    [InlineData("ali@afirma.com")]
    [InlineData("ALI@AFirma.COM")]
    [InlineData("ali@afirma.com.tr")]
    public async Task AnAddressAtOneOfTheCompanysDomainsIsTaken(string address)
    {
        await using var api = VerificationTestApi.Start(database);
        var (_, code) = await api.CompanyAsync("afirma.com", "afirma.com.tr");
        var email = Unique(address);

        using var response = await api.RegisterAdminAsync(code, email);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData("ali@gmail.com")]
    [InlineData("ali@mail.afirma.com")]
    [InlineData("ali@xafirma.com")]
    [InlineData("ali@afirma.co")]
    public async Task AnAddressAtAnotherDomainIsRefusedAndTheDomainsAreNamed(string address)
    {
        await using var api = VerificationTestApi.Start(database);
        var (_, code) = await api.CompanyAsync("afirma.com", "afirma.com.tr");
        var email = Unique(address);

        using var response = await api.RegisterAdminAsync(code, email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["afirma.com", "afirma.com.tr"],
            json.RootElement.GetProperty("emailDomains").EnumerateArray().Select(e => e.GetString()));
        Assert.Null(await api.UserAsync(email));
        Assert.Empty(api.Email.SentTo(email));
    }

    [Fact]
    public async Task ACompanyWithoutDomainsTakesAnyAddress()
    {
        await using var api = VerificationTestApi.Start(database);
        var (_, code) = await api.CompanyAsync();

        using var response = await api.RegisterAdminAsync(code, Unique("ali@gmail.com"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // ---- An address that is already registered ----

    [Fact]
    public async Task ARegisteredAddressWithAValidCodeIsAConflict()
    {
        await using var api = VerificationTestApi.Start(database);
        var (_, code) = await api.CompanyAsync();
        var email = TestAccounts.NewEmail();
        using (await TestAccounts.RegisterAsync(api.Client, email))
        {
        }

        using var response = await api.RegisterAdminAsync(code, email.ToUpperInvariant());

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "This email address is already registered.");
    }

    // Only someone with a valid code, and an address the company takes, learns that an
    // address is registered.
    [Fact]
    public async Task AWrongCodeSaysNothingAboutARegisteredAddress()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = TestAccounts.NewEmail();
        using (await TestAccounts.RegisterAsync(api.Client, email))
        {
        }

        using var response = await api.RegisterAdminAsync("AAAA-BBBB-CCCC", email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnAddressOutsideTheRuleSaysNothingAboutWhetherItIsRegistered()
    {
        await using var api = VerificationTestApi.Start(database);
        var (_, code) = await api.CompanyAsync("afirma.com");
        var email = TestAccounts.NewEmail(); // @example.com
        using (await TestAccounts.RegisterAsync(api.Client, email))
        {
        }

        using var response = await api.RegisterAdminAsync(code, email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Two registrations of the same address at the same moment: one account, the other 409.
    [Fact]
    public async Task TheSameAddressRegisteredTwiceAtOnceMakesOneAccount()
    {
        await using var api = VerificationTestApi.Start(database);
        var (_, code) = await api.CompanyAsync();
        var email = TestAccounts.NewEmail();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => api.RegisterAdminAsync(code, email)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Accepted), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    // ---- When the e-mail cannot be sent ----

    // The account stays (registering again would only answer 409); the screen says the code
    // did not go out and offers a new one.
    [Fact]
    public async Task WhenTheCodeCannotBeSentTheAccountStaysAndSaysSo()
    {
        await using var api = VerificationTestApi.Start(database);
        var (_, code) = await api.CompanyAsync();
        var email = TestAccounts.NewEmail();
        api.Email.Failing = true;

        using var response = await api.RegisterAdminAsync(code, email);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<AdminRegistrationResponse>())!.VerificationSent);
        Assert.NotNull(await api.UserAsync(email));
        Assert.Null(await api.VerificationAsync(email));

        // Asking again works at once: the code that never left did not start the wait.
        api.Email.Failing = false;
        using var resend = await api.ResendAsync(email);
        Assert.Equal(HttpStatusCode.NoContent, resend.StatusCode);
        using var verify = await api.VerifyAsync(email, api.Email.LastCodeFor(email));
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }

    // ---- Input ----

    [Theory]
    [InlineData("""{ "email": "a@example.com", "password": "Gizli123!" }""")]
    [InlineData("""{ "registrationCode": "AAAA-BBBB-CCCC", "password": "Gizli123!" }""")]
    [InlineData("""{ "registrationCode": "AAAA-BBBB-CCCC", "email": "a@example.com" }""")]
    [InlineData("""{ "registrationCode": "AAAA-BBBB-CCCC", "email": "not an address", "password": "Gizli123!" }""")]
    [InlineData("""{ "registrationCode": "AAAA-BBBB-CCCC", "email": "a@example.com", "password": "kisa" }""")]
    [InlineData("""{ "registrationCode": "AAAA-BBBB-CCCC-DDDD-EEEE-FFFF-GGGG", "email": "a@example.com", "password": "Gizli123!" }""")]
    public async Task MissingOrInvalidFieldsAreRejectedAsInvalidInput(string body)
    {
        await using var api = VerificationTestApi.Start(database);

        using var response = await api.Client.PostAsync(
            "/auth/register-admin",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>();
        Assert.NotEmpty(problem!.Errors);
    }

    // The endpoint does not depend on open registration (decisions #70): it is how admins
    // register in production, where open registration is off.
    [Fact]
    public async Task ItWorksWithOpenRegistrationOff()
    {
        await using var api = VerificationTestApi.Start(database, openRegistration: false);
        var (_, code) = await api.CompanyAsync();

        using var response = await api.RegisterAdminAsync(code, TestAccounts.NewEmail());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // ---- Helpers ----

    // A unique address at the given domain: "ali@afirma.com" becomes "ali-<guid>@afirma.com".
    private static string Unique(string address)
    {
        var at = address.IndexOf('@');
        return $"{address[..at]}-{Guid.NewGuid():N}{address[at..]}";
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(title, problem!.Title);
    }
}
