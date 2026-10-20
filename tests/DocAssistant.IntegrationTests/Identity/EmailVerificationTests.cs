using System.Net;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.Net.Http.Headers;

namespace DocAssistant.IntegrationTests.Identity;

// POST /auth/verify-email (docs/decisions.md #70): the six-digit code e-mailed to a new
// account, typed back. The right one verifies the account and signs the person in; every
// other answer is the same 400, so it says nothing about the account. A code works for 15
// minutes and for 5 tries, and a new code replaces the old one.
[Collection(DatabaseCollection.Name)]
public class EmailVerificationTests(PostgresFixture database)
{
    private const string Refused = "The code is wrong or no longer valid.";

    // ---- The right code ----

    [Fact]
    public async Task TheRightCodeVerifiesTheAccountAndSignsIn()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();

        using var response = await api.VerifyAsync(email, api.Email.LastCodeFor(email));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await TestAccounts.ReadTokenAsync(response);

        // Signed in to the company the code belongs to, as its admin.
        using var me = await TestAccounts.GetCurrentUserAsync(api.Client, token);
        var user = await me.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.Equal(email, user!.Email);
        Assert.Equal((await api.MembershipAsync(email))!.TenantId, user.TenantId);
        Assert.Equal("Admin", user.Role);

        // As at login (#60), the browser gets the session in a cookie too.
        Assert.Contains(response.Headers.GetValues(HeaderNames.SetCookie), cookie => cookie.StartsWith(SessionCookie.Name + "="));
    }

    [Fact]
    public async Task TheRightCodeMarksTheAddressVerifiedAndIsUsedUp()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        var code = api.Email.LastCodeFor(email);

        using (await api.VerifyAsync(email, code))
        {
        }

        Assert.NotNull((await api.UserAsync(email))!.EmailVerifiedAt);
        Assert.Null(await api.VerificationAsync(email));

        // A code works once.
        using var again = await api.VerifyAsync(email, code);
        await AssertRefusedAsync(again);
    }

    [Fact]
    public async Task AfterVerifyingThePersonCanSignInWithThePassword()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();

        using (await api.VerifyAsync(email, api.Email.LastCodeFor(email)))
        {
        }

        using var login = await api.LoginAsync(email);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // People copy codes with spaces ("123 456"); the address is matched as at login.
    [Fact]
    public async Task SpacesInTheCodeAndTheCaseOfTheAddressDoNotCount()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        var code = api.Email.LastCodeFor(email);

        using var response = await api.VerifyAsync(email.ToUpperInvariant(), $" {code[..3]} {code[3..]} ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Refusals, all alike ----

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("١٢٣٤٥٦")]
    public async Task ACodeThatIsNotSixDigitsIsRefused(string code)
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();

        using var response = await api.VerifyAsync(email, code);

        await AssertRefusedAsync(response);
        Assert.Null((await api.UserAsync(email))!.EmailVerifiedAt);
    }

    [Fact]
    public async Task AWrongCodeIsRefused()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();

        using var response = await api.VerifyAsync(email, OtherCode(api.Email.LastCodeFor(email)));

        await AssertRefusedAsync(response);
        Assert.Null((await api.UserAsync(email))!.EmailVerifiedAt);
    }

    [Fact]
    public async Task ACodeOlderThanFifteenMinutesIsRefused()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        await api.AgeVerificationAsync(email, EmailVerificationService.CodeLifetime + TimeSpan.FromSeconds(1));

        using var response = await api.VerifyAsync(email, api.Email.LastCodeFor(email));

        await AssertRefusedAsync(response);
    }

    [Fact]
    public async Task ACodeJustUnderFifteenMinutesOldStillWorks()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        await api.AgeVerificationAsync(email, EmailVerificationService.CodeLifetime - TimeSpan.FromSeconds(30));

        using var response = await api.VerifyAsync(email, api.Email.LastCodeFor(email));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Five wrong tries use the code up: after them even the right one is refused.
    [Fact]
    public async Task AfterFiveWrongCodesTheRightOneIsRefusedToo()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        var code = api.Email.LastCodeFor(email);

        for (var i = 0; i < EmailVerificationService.MaxAttempts; i++)
        {
            using (await api.VerifyAsync(email, OtherCode(code)))
            {
            }
        }

        using var response = await api.VerifyAsync(email, code);

        await AssertRefusedAsync(response);
        Assert.Null((await api.UserAsync(email))!.EmailVerifiedAt);
    }

    [Fact]
    public async Task FourWrongCodesStillLeaveTheRightOneWorking()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        var code = api.Email.LastCodeFor(email);

        for (var i = 0; i < EmailVerificationService.MaxAttempts - 1; i++)
        {
            using (await api.VerifyAsync(email, OtherCode(code)))
            {
            }
        }

        using var response = await api.VerifyAsync(email, code);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // A code that is not six digits is turned away before it is compared: it does not use
    // up one of the five tries.
    [Fact]
    public async Task MalformedCodesDoNotUseUpTries()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();

        for (var i = 0; i < EmailVerificationService.MaxAttempts + 2; i++)
        {
            using (await api.VerifyAsync(email, "abc"))
            {
            }
        }

        Assert.Equal(0, (await api.VerificationAsync(email))!.FailedAttempts);
        using var response = await api.VerifyAsync(email, api.Email.LastCodeFor(email));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Two guesses at the same moment cannot both slip under the limit.
    [Fact]
    public async Task GuessesSentAtOnceStillCountOneByOne()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        var code = api.Email.LastCodeFor(email);

        var guesses = Enumerable.Range(0, 12).Select(_ => api.VerifyAsync(email, OtherCode(code)));
        foreach (var response in await Task.WhenAll(guesses))
        {
            response.Dispose();
        }

        Assert.Equal(EmailVerificationService.MaxAttempts, (await api.VerificationAsync(email))!.FailedAttempts);
    }

    // The answer for an address without an account is the answer for a wrong code: the
    // endpoint does not tell which addresses are registered.
    [Fact]
    public async Task AnUnknownAddressGetsTheSameAnswerAsAWrongCode()
    {
        await using var api = VerificationTestApi.Start(database);

        using var response = await api.VerifyAsync(TestAccounts.NewEmail(), "123456");

        await AssertRefusedAsync(response);
    }

    [Fact]
    public async Task AnAccountThatIsAlreadyVerifiedGetsTheSameAnswer()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = TestAccounts.NewEmail();

        // Open registration makes accounts that count as verified (decisions #70).
        using (await TestAccounts.RegisterAsync(api.Client, email))
        {
        }

        using var response = await api.VerifyAsync(email, "123456");

        await AssertRefusedAsync(response);
    }

    [Fact]
    public async Task APlatformAdminHasNoCodeToVerify()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = TestAccounts.NewEmail();
        await MakeUnverifiedPlatformAdminAsync(email);

        using var resend = await api.ResendAsync(email);
        using var verify = await api.VerifyAsync(email, "123456");

        Assert.Equal(HttpStatusCode.NoContent, resend.StatusCode);
        Assert.Empty(api.Email.SentTo(email));
        await AssertRefusedAsync(verify);
    }

    // ---- A new code ----

    [Fact]
    public async Task ANewCodeReplacesTheOldOne()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        var first = api.Email.LastCodeFor(email);
        await api.AgeVerificationAsync(email, EmailVerificationService.ResendCooldown);

        using (await api.ResendAsync(email))
        {
        }

        var second = api.Email.LastCodeFor(email);
        Assert.Equal(2, api.Email.SentTo(email).Count);

        // The two codes are drawn at random and may, rarely, be the same.
        if (first != second)
        {
            using var old = await api.VerifyAsync(email, first);
            await AssertRefusedAsync(old);
        }

        using var current = await api.VerifyAsync(email, second);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [Fact]
    public async Task ANewCodeStartsTheTriesOver()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        var first = api.Email.LastCodeFor(email);

        for (var i = 0; i < EmailVerificationService.MaxAttempts; i++)
        {
            using (await api.VerifyAsync(email, OtherCode(first)))
            {
            }
        }

        await api.AgeVerificationAsync(email, EmailVerificationService.ResendCooldown);
        using (await api.ResendAsync(email))
        {
        }

        using var response = await api.VerifyAsync(email, api.Email.LastCodeFor(email));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- What is kept ----

    // Only the code's SHA-256 is stored, as with invitation keys (#67).
    [Fact]
    public async Task TheCodeIsKeptOnlyAsAHash()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        var code = api.Email.LastCodeFor(email);

        var verification = (await api.VerificationAsync(email))!;

        Assert.DoesNotContain(code, verification.CodeHash);
        Assert.Matches("^[0-9a-f]{64}$", verification.CodeHash);
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code))),
            verification.CodeHash);
    }

    [Theory]
    [InlineData("""{ "code": "123456" }""")]
    [InlineData("""{ "email": "a@example.com" }""")]
    [InlineData("""{ "email": "a@example.com", "code": "123456789012345678901" }""")]
    public async Task MissingOrOversizedFieldsAreRejectedAsInvalidInput(string body)
    {
        await using var api = VerificationTestApi.Start(database);

        using var response = await api.Client.PostAsync(
            "/auth/verify-email",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Helpers ----

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(Refused, problem!.Title);
        Assert.False(response.Headers.TryGetValues(HeaderNames.SetCookie, out _));
    }

    // Six digits that are not the code.
    private static string OtherCode(string code) => code == "000000" ? "000001" : "000000";

    private async Task MakeUnverifiedPlatformAdminAsync(string email)
    {
        await using var db = database.CreateOwnerDbContext();
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = User.NormalizeEmail(email),
            PasswordHash = "not used",
            IsPlatformAdmin = true,
        });
        await db.SaveChangesAsync();
    }
}
