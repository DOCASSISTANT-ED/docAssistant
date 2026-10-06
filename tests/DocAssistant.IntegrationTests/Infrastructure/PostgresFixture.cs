using DocAssistant.Api.Data;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DocAssistant.IntegrationTests.Infrastructure;

// A throwaway PostgreSQL built like the dev database: same image, same init scripts
// (which create docassistant_app) and all migrations (docs/decisions.md #18, #19).
// Shared by every test in the Database collection, so the container starts once per run.
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string AppUser = "docassistant_app";

    private const string AppPassword = "test-app-password";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("docassistant")
        .WithUsername("docassistant")
        .WithPassword("test-superuser-password")
        .WithEnvironment("APP_DB_PASSWORD", AppPassword)
        .WithResourceMapping(new DirectoryInfo(FindInitScriptsDirectory()), "/docker-entrypoint-initdb.d/")
        .Build();

    // Database owner (superuser): migrations and test setup only.
    public string SuperuserConnectionString => _container.GetConnectionString();

    // The restricted user the running app connects as; subject to Row-Level Security.
    public string AppConnectionString =>
        new NpgsqlConnectionStringBuilder(SuperuserConnectionString)
        {
            Username = AppUser,
            Password = AppPassword,
        }.ConnectionString;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Like `dotnet ef database update`: migrations run as the database owner.
        await using var db = CreateOwnerDbContext();
        await db.Database.MigrateAsync();
    }

    // Bypasses Row-Level Security (superuser). For setup and checks only, never for
    // asserting tenant isolation.
    public AppDbContext CreateOwnerDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseAppDatabase(SuperuserConnectionString);

        return new AppDbContext(options.Options, new NoTenantContext());
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    // Walks up from the test output folder (tests/.../bin/Debug/net10.0) to the repo root.
    private static string FindInitScriptsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var initScripts = Path.Combine(dir.FullName, "docker", "postgres", "init");
            if (Directory.Exists(initScripts))
            {
                return initScripts;
            }
        }

        throw new DirectoryNotFoundException("docker/postgres/init was not found above the test output folder.");
    }

    private sealed class NoTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Database";
}
