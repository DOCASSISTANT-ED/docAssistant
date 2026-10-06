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

    // The API running in memory against this database as the restricted user. Shared, so
    // it starts once per test run. Tests that need different services can derive one with
    // Api.WithWebHostBuilder(...).
    public DocAssistantApiFactory Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Like `dotnet ef database update`: migrations run as the database owner.
        await using (var db = CreateOwnerDbContext())
        {
            await db.Database.MigrateAsync();
        }

        Api = new DocAssistantApiFactory(AppConnectionString);
    }

    // Bypasses Row-Level Security (superuser). For setup and checks, and for testing the
    // query filters on their own (with a tenantId, the filters are the only active layer).
    // Never use it to assert that RLS isolates tenants.
    public AppDbContext CreateOwnerDbContext(Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseAppDatabase(SuperuserConnectionString);

        return new AppDbContext(options.Options, new FixedTenantContext(tenantId));
    }

    // Wired like the running app (Program.cs): restricted user, query filters, write rules
    // and the interceptor that hands the tenant to Row-Level Security. Null = no tenant
    // selected, as before login. connectionString overrides AppConnectionString (it must
    // still log in as the app user).
    public AppDbContext CreateAppDbContext(Guid? tenantId, string? connectionString = null)
    {
        var tenantContext = new FixedTenantContext(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseAppDatabase(connectionString ?? AppConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor(tenantContext));

        return new AppDbContext(options.Options, tenantContext);
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await _container.DisposeAsync();
    }

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

    private sealed class FixedTenantContext(Guid? tenantId) : ITenantContext
    {
        public Guid? TenantId => tenantId;
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Database";
}
