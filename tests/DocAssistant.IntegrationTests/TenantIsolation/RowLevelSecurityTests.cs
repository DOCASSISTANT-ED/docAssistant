using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocAssistant.IntegrationTests.TenantIsolation;

// Layer 2 of tenant isolation: PostgreSQL Row-Level Security on documents
// (docs/decisions.md #15). Every test uses the app's own connection (restricted user) and
// deliberately goes around the application layer, with IgnoreQueryFilters() or raw SQL,
// so the database is the only thing standing between the tenants.
[Collection(DatabaseCollection.Name)]
public class RowLevelSecurityTests(PostgresFixture database) : IAsyncLifetime
{
    private const string RlsViolation = PostgresErrorCodes.InsufficientPrivilege; // 42501

    private TenantIsolationScenario _scenario = null!;

    public async Task InitializeAsync() => _scenario = await TenantIsolationScenario.CreateAsync(database);

    public Task DisposeAsync() => Task.CompletedTask;

    // A developer "forgets" the filter.
    [Fact]
    public async Task IgnoringQueryFiltersStillShowsOnlyOwnDocuments()
    {
        await using var db = database.CreateAppDbContext(_scenario.TenantA);

        var documents = await db.Documents.IgnoreQueryFilters().Select(d => d.Id).ToListAsync();

        Assert.Equivalent(new[] { _scenario.DocumentA1, _scenario.DocumentA2 }, documents);
    }

    // Raw SQL never sees EF Core's filters (as the Phase 3 search queries will be).
    [Fact]
    public async Task RawSqlShowsOnlyOwnDocuments()
    {
        await using var db = database.CreateAppDbContext(_scenario.TenantA);

        var documents = await db.Database
            .SqlQuery<Guid>($"""SELECT id AS "Value" FROM documents""")
            .ToListAsync();

        Assert.Equivalent(new[] { _scenario.DocumentA1, _scenario.DocumentA2 }, documents);
    }

    [Fact]
    public async Task RawSqlShowsNothingWithoutSelectedTenant()
    {
        await using var db = database.CreateAppDbContext(tenantId: null);

        var count = await db.Database
            .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM documents""")
            .SingleAsync();

        Assert.Equal(0, count);
    }

    // WITH CHECK: rows can only be written for the current tenant.
    [Fact]
    public async Task RawSqlCannotInsertDocumentForAnotherTenant()
    {
        await using var db = database.CreateAppDbContext(_scenario.TenantA);

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO documents (id, tenant_id, title, status) VALUES ({Guid.CreateVersion7()}, {_scenario.TenantB}, 'planted', 'Pending')"));

        Assert.Equal(RlsViolation, error.SqlState);
    }

    // USING: rows of other tenants are invisible to UPDATE and DELETE, so nothing matches.
    [Fact]
    public async Task RawSqlCannotChangeOrDeleteAnotherTenantsDocument()
    {
        await using (var db = database.CreateAppDbContext(_scenario.TenantA))
        {
            var updated = await db.Database.ExecuteSqlAsync(
                $"UPDATE documents SET title = 'hijacked' WHERE id = {_scenario.DocumentB1}");
            var deleted = await db.Database.ExecuteSqlAsync(
                $"DELETE FROM documents WHERE id = {_scenario.DocumentB1}");

            Assert.Equal(0, updated);
            Assert.Equal(0, deleted);
        }

        await using var owner = database.CreateOwnerDbContext();
        var documentB1 = await owner.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == _scenario.DocumentB1);
        Assert.Equal("B-1", documentB1.Title);
    }

    // ExecuteUpdate skips SaveChanges, so the application's write rules do not see it and
    // the query filter only limits which rows are updated. Moving rows to another tenant
    // is stopped by RLS's WITH CHECK alone.
    [Fact]
    public async Task BulkUpdateCannotMoveDocumentsToAnotherTenant()
    {
        await using (var db = database.CreateAppDbContext(_scenario.TenantA))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Documents.ExecuteUpdateAsync(u => u.SetProperty(d => d.TenantId, _scenario.TenantB)));

            Assert.Equal(RlsViolation, error.SqlState);
        }

        await using var owner = database.CreateOwnerDbContext();
        var stillInA = await owner.Documents.IgnoreQueryFilters()
            .CountAsync(d => d.TenantId == _scenario.TenantA);
        Assert.Equal(2, stillInA);
    }

    // Pooled connections are reused across requests. With Npgsql's own reset switched off
    // and a single pooled connection, only TenantConnectionInterceptor can stop the
    // previous request's tenant from leaking into the next one.
    [Fact]
    public async Task PreviousTenantDoesNotLeakThroughAPooledConnection()
    {
        var singleConnectionPool = new NpgsqlConnectionStringBuilder(database.AppConnectionString)
        {
            MaxPoolSize = 1,
            NoResetOnClose = true,
            ApplicationName = $"pool-test-{Guid.NewGuid():N}", // a pool of its own
        }.ConnectionString;

        int firstBackend;
        await using (var asTenantA = database.CreateAppDbContext(_scenario.TenantA, singleConnectionPool))
        {
            firstBackend = await BackendProcessId(asTenantA);
            Assert.NotEmpty(await asTenantA.Documents.IgnoreQueryFilters().ToListAsync());
        }

        await using var withoutTenant = database.CreateAppDbContext(tenantId: null, singleConnectionPool);
        Assert.Equal(firstBackend, await BackendProcessId(withoutTenant)); // same physical connection
        Assert.Empty(await withoutTenant.Documents.IgnoreQueryFilters().ToListAsync());
    }

    private static Task<int> BackendProcessId(Api.Data.AppDbContext db) =>
        db.Database.SqlQuery<int>($"""SELECT pg_backend_pid() AS "Value" """).SingleAsync();
}
