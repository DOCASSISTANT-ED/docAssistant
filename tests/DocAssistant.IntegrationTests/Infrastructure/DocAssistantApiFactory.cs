using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocAssistant.IntegrationTests.Infrastructure;

// Runs the real API in memory (same Program.cs, middleware and endpoints) against the
// test database. Requests from CreateClient() never leave the process.
public sealed class DocAssistantApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    // Test-only key; tests that need to forge or inspect tokens can use it.
    public const string SigningKey = "integration-tests-signing-key-not-a-secret-0123456789";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing", not "Development": no user-secrets and no appsettings.Development.json,
        // so tests never depend on a developer's machine or touch the dev database.
        builder.UseEnvironment("Testing");

        // UseSetting, because Program.cs reads these while the app is still being built.
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Jwt:SigningKey", SigningKey);
    }
}
