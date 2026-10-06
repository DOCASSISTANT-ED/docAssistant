using System.Net;
using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocAssistant.IntegrationTests.Infrastructure;

// Checks that the in-memory API is wired to the test database and test settings;
// feature tests rely on it.
[Collection(DatabaseCollection.Name)]
public class DocAssistantApiFactoryTests(PostgresFixture database)
{
    [Fact]
    public async Task ApiStarts()
    {
        using var client = database.Api.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Tenant isolation tests are only meaningful if the API runs as the restricted user;
    // as the superuser, Row-Level Security would be bypassed.
    [Fact]
    public async Task ApiConnectsToTestDatabaseAsRestrictedUser()
    {
        await using var scope = database.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var currentUser = await db.Database
            .SqlQueryRaw<string>("""SELECT current_user AS "Value" """)
            .SingleAsync();

        Assert.Equal(PostgresFixture.AppUser, currentUser);
    }

    // Tests must not depend on a developer's user-secrets.
    [Fact]
    public void ApiUsesTestSigningKey()
    {
        var jwt = database.Api.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

        Assert.Equal(DocAssistantApiFactory.SigningKey, jwt.SigningKey);
    }
}
