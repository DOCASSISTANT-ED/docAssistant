using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.Net.Http.Headers;

namespace DocAssistant.IntegrationTests.Identity;

// POST /auth/login for an account whose address is not verified (docs/decisions.md #70):
// refused with 403 after the password is checked, so only someone who knows the password
// learns that the account is unverified. The answer carries emailNotVerified and whether
// a new code was sent; a new code is sent within the e-mail limits.
[Collection(DatabaseCollection.Name)]
public class LoginVerificationTests(PostgresFixture database)
{
    [Fact]
    public async Task AnUnverifiedAccountIsRefusedAndGetsANewCode()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        await api.AgeVerificationAsync(email, EmailVerificationService.ResendCooldown);

        using var response = await api.LoginAsync(email);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("This account's email address is not verified yet.", json.RootElement.GetProperty("title").GetString());
        Assert.True(json.RootElement.GetProperty("emailNotVerified").GetBoolean());
        Assert.True(json.RootElement.GetProperty("verificationSent").GetBoolean());
        Assert.False(response.Headers.TryGetValues(HeaderNames.SetCookie, out _));

        // The new code is the one that works.
        Assert.Equal(2, api.Email.SentTo(email).Count);
        using var verify = await api.VerifyAsync(email, api.Email.LastCodeFor(email));
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }

    // Right after registering, the code from registration is still within its minute: no
    // new one goes out, and the answer says so.
    [Fact]
    public async Task WithinAMinuteOfTheLastCodeNoNewOneIsSent()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();

        using var response = await api.LoginAsync(email);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("emailNotVerified").GetBoolean());
        Assert.False(json.RootElement.GetProperty("verificationSent").GetBoolean());
        Assert.Single(api.Email.SentTo(email));
    }

    [Fact]
    public async Task WhenTheCodeCannotBeSentTheAnswerSaysSo()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        await api.AgeVerificationAsync(email, EmailVerificationService.ResendCooldown);
        api.Email.Failing = true;

        using var response = await api.LoginAsync(email);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("verificationSent").GetBoolean());
    }

    // A wrong password gets the usual 401: it does not tell that the account exists and is
    // unverified, and no code is sent.
    [Fact]
    public async Task AWrongPasswordSaysNothingAboutVerification()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        await api.AgeVerificationAsync(email, EmailVerificationService.ResendCooldown);

        using var response = await api.LoginAsync(email, "Yanlis-Parola-1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("emailNotVerified", await response.Content.ReadAsStringAsync());
        Assert.Single(api.Email.SentTo(email));
    }

    [Fact]
    public async Task AVerifiedAccountSignsInAsBefore()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        using (await api.VerifyAsync(email, api.Email.LastCodeFor(email)))
        {
        }

        using var response = await api.LoginAsync(email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await TestAccounts.ReadTokenAsync(response);
        using var me = await TestAccounts.GetCurrentUserAsync(api.Client, token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    // Accounts from open registration (development, tests, the evaluation run) count as
    // verified, as there is no mailbox there to read a code from.
    [Fact]
    public async Task AnAccountFromOpenRegistrationSignsInWithoutACode()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = TestAccounts.NewEmail();
        using (await TestAccounts.RegisterAsync(api.Client, email))
        {
        }

        using var response = await api.LoginAsync(email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await api.UserAsync(email))!.EmailVerifiedAt);
        Assert.Empty(api.Email.SentTo(email));
    }

    // The check comes before the platform admin's own way in (#70): an unverified platform
    // admin is refused like anyone else, and gets no code (a platform admin is made on the
    // server and verified there).
    [Fact]
    public async Task AnUnverifiedPlatformAdminIsRefused()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = TestAccounts.NewEmail();
        await using (var db = database.CreateOwnerDbContext())
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                NormalizedEmail = User.NormalizeEmail(email),
                PasswordHash = string.Empty,
                IsPlatformAdmin = true,
            };
            user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(user, VerificationTestApi.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        using var response = await api.LoginAsync(email);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("emailNotVerified", await response.Content.ReadAsStringAsync());
    }

    // ---- Known gaps ----------------------------------------------------------------------
    // Found while writing these tests. The test states the behaviour that seems right and is
    // skipped because the code does not do it yet; remove Skip when it is fixed (or change
    // the expectation if another behaviour is chosen).

    private const string KnownGap = "Known gap in AuthEndpoints.Login; see the pull request that added this test.";

    // The refused sign-in e-mails an unverified platform admin a code, but POST
    // /auth/verify-email never takes a platform admin's code (EmailVerificationEndpoints
    // leaves platform admins out): the e-mail can never be used. Hard to reach, as the
    // command line makes platform admins verified, but the e-mail should not go out.
    [Fact(Skip = KnownGap)]
    public async Task AnUnverifiedPlatformAdminIsSentNoUselessCode()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = TestAccounts.NewEmail();
        await using (var db = database.CreateOwnerDbContext())
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                NormalizedEmail = User.NormalizeEmail(email),
                PasswordHash = string.Empty,
                IsPlatformAdmin = true,
            };
            user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(user, VerificationTestApi.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        using (await api.LoginAsync(email))
        {
        }

        Assert.Empty(api.Email.SentTo(email));
    }
}
