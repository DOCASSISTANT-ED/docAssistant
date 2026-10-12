using DocAssistant.Api.Data;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DocAssistant.Eval;

// A throwaway PostgreSQL for one run, built like the development database: same image,
// same init scripts (which create docassistant_app) and all migrations. The development
// database is never touched, and every run starts from an empty one.
internal sealed class EvalDatabase : IAsyncDisposable
{
    private const string AppUser = "docassistant_app";
    private const string AppPassword = "eval-app-password";

    private readonly PostgreSqlContainer _container;

    private EvalDatabase(PostgreSqlContainer container)
    {
        _container = container;
    }

    // The restricted user the app connects as, so Row-Level Security applies as in production.
    public string AppConnectionString =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = AppUser,
            Password = AppPassword,
        }.ConnectionString;

    public static async Task<EvalDatabase> StartAsync(RepoPaths paths)
    {
        var container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithDatabase("docassistant")
            .WithUsername("docassistant")
            .WithPassword("eval-superuser-password")
            .WithEnvironment("APP_DB_PASSWORD", AppPassword)
            .WithResourceMapping(new DirectoryInfo(paths.DatabaseInitScripts), "/docker-entrypoint-initdb.d/")
            // The container's start-up log would bury the report.
            .WithLogger(NullLogger.Instance)
            .Build();

        await container.StartAsync();

        // Like `dotnet ef database update`: migrations run as the database owner.
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseAppDatabase(container.GetConnectionString());

        await using (var db = new AppDbContext(options.Options, new NoTenant()))
        {
            await db.Database.MigrateAsync();
        }

        return new EvalDatabase(container);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => null;
    }
}
