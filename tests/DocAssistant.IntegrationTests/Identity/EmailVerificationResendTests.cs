using System.Net;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;

namespace DocAssistant.IntegrationTests.Identity;

// POST /auth/verify-email/resend (docs/decisions.md #70): a new code for an account that
// is not verified yet. Always 204, whether the address has an account, is verified or
// asked too soon, so the answer says nothing about the address; only an e-mail the server
// would not take is reported (503). A mailbox gets a new code at most once a minute and
// five times an hour.
[Collection(DatabaseCollection.Name)]
public class EmailVerificationResendTests(PostgresFixture database)
{
    // ---- What goes out ----

    [Fact]
    public async Task AfterAMinuteANewCodeIsSent()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        await api.AgeVerificationAsync(email, EmailVerificationService.ResendCooldown);

        using var response = await api.ResendAsync(email);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, api.Email.SentTo(email).Count);
    }

    [Fact]
    public async Task TheEmailNamesTheCodeAndHowLongItWorks()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();

        var message = Assert.Single(api.Email.SentTo(email));

        Assert.Equal(email, message.To);
        Assert.Equal("DocAssistant doğrulama kodunuz", message.Subject);
        Assert.Contains(api.Email.LastCodeFor(email), message.Body);
        Assert.Contains("15 dakika", message.Body);
    }

    // ---- When nothing goes out, still 204 ----

    [Fact]
    public async Task WithinAMinuteNoNewCodeIsSent()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        var before = await api.VerificationAsync(email);

        using var response = await api.ResendAsync(email);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(api.Email.SentTo(email));

        // The code that was sent still holds.
        Assert.Equal(before!.CodeHash, (await api.VerificationAsync(email))!.CodeHash);
        using var verify = await api.VerifyAsync(email, api.Email.LastCodeFor(email));
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }

    [Fact]
    public async Task AnUnknownAddressGetsTheSameAnswer()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = TestAccounts.NewEmail();

        using var response = await api.ResendAsync(email);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(api.Email.SentTo(email));
    }

    [Fact]
    public async Task AVerifiedAccountGetsTheSameAnswerAndNoEmail()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = TestAccounts.NewEmail();
        using (await TestAccounts.RegisterAsync(api.Client, email))
        {
        }

        using var response = await api.ResendAsync(email);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(api.Email.SentTo(email));
        Assert.Null(await api.VerificationAsync(email));
    }

    // ---- Limits ----

    // Five codes in an hour, the one from registration included; the sixth waits for the
    // next hour, even with a minute between each.
    [Fact]
    public async Task AtMostFiveCodesGoOutInAnHour()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();

        for (var i = 0; i < EmailVerificationService.MaxSendsPerHour; i++)
        {
            await api.AgeVerificationAsync(email, EmailVerificationService.ResendCooldown);
            using (await api.ResendAsync(email))
            {
            }
        }

        Assert.Equal(EmailVerificationService.MaxSendsPerHour, api.Email.SentTo(email).Count);
    }

    [Fact]
    public async Task ANewHourStartsItsOwnCount()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();

        for (var i = 0; i < EmailVerificationService.MaxSendsPerHour; i++)
        {
            await api.AgeVerificationAsync(email, EmailVerificationService.ResendCooldown);
            using (await api.ResendAsync(email))
            {
            }
        }

        await api.AgeVerificationAsync(email, TimeSpan.FromHours(1));
        using var response = await api.ResendAsync(email);

        Assert.Equal(EmailVerificationService.MaxSendsPerHour + 1, api.Email.SentTo(email).Count);
        var verification = (await api.VerificationAsync(email))!;
        Assert.Equal(1, verification.SendsInWindow);
    }

    // ---- When the e-mail cannot be sent ----

    [Fact]
    public async Task AnEmailTheServerWillNotTakeIsReportedAndNothingIsKept()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        await api.AgeVerificationAsync(email, EmailVerificationService.ResendCooldown);
        var before = (await api.VerificationAsync(email))!;
        api.Email.Failing = true;

        using var response = await api.ResendAsync(email);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // The code that went out before still holds, and the count did not move.
        var after = (await api.VerificationAsync(email))!;
        Assert.Equal(before.CodeHash, after.CodeHash);
        Assert.Equal(before.SendsInWindow, after.SendsInWindow);
        Assert.Equal(before.SentAt, after.SentAt);
    }

    // A code that never left must not start the one-minute wait.
    [Fact]
    public async Task AfterAFailedSendThePersonCanAskAgainAtOnce()
    {
        await using var api = VerificationTestApi.Start(database);
        var email = await api.UnverifiedAdminAsync();
        await api.AgeVerificationAsync(email, EmailVerificationService.ResendCooldown);

        api.Email.Failing = true;
        using (await api.ResendAsync(email))
        {
        }

        api.Email.Failing = false;
        using var response = await api.ResendAsync(email);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, api.Email.SentTo(email).Count);
        using var verify = await api.VerifyAsync(email, api.Email.LastCodeFor(email));
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }

    [Fact]
    public async Task AMissingAddressIsRejectedAsInvalidInput()
    {
        await using var api = VerificationTestApi.Start(database);

        using var response = await api.Client.PostAsync(
            "/auth/verify-email/resend",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
