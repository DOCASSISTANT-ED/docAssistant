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

        // Open registration (decisions #70) is how most tests make a company; tests of the
        // closed setting turn it off.
        builder.UseSetting("Auth:OpenRegistration", "true");

        // The app refuses to start without storage settings. These point nowhere on
        // purpose (.invalid never resolves): no test may reach a real bucket by accident.
        // Tests that upload replace IFileStorage or supply their own storage.
        builder.UseSetting("Storage:ServiceUrl", "http://storage.invalid");
        builder.UseSetting("Storage:AccessKey", "integration-tests");
        builder.UseSetting("Storage:SecretKey", "integration-tests");
        builder.UseSetting("Storage:Bucket", "integration-tests");

        // Same for the embedding model: tests never call a real one (docs/decisions.md #50).
        // Tests that embed replace IEmbeddingGenerator.
        builder.UseSetting("Embedding:BaseUrl", "http://embedding.invalid");
        builder.UseSetting("Embedding:Model", "integration-tests");

        // Re-indexing at startup (decisions #54) is off: tests leave chunks without
        // embeddings on purpose, and every copy of the app a test starts would otherwise
        // try to embed the chunks of all the others. Re-indexing tests run it themselves.
        builder.UseSetting("Embedding:ReindexOnStartup", "false");

        // And for the chat model (decisions #61). Tests that ask questions replace IChatClient.
        builder.UseSetting("Chat:BaseUrl", "http://chat.invalid");
        builder.UseSetting("Chat:Model", "integration-tests");

        // And for e-mail (decisions #70): no test may send a real one. Tests that send
        // e-mail replace IEmailSender.
        builder.UseSetting("Email:Host", "smtp.invalid");
        builder.UseSetting("Email:FromAddress", "integration-tests@example.com");
    }
}
