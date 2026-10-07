using DocAssistant.IntegrationTests.Documents;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.TenantIsolation;

// Layer 1 of tenant isolation for writes: AppDbContext's tenant write rules for
// ITenantOwned entities (docs/decisions.md #14). RLS's WITH CHECK would also block these
// writes, so the tests use the superuser connection with a tenant selected: RLS is
// bypassed and the application rules are the only layer being tested.
[Collection(DatabaseCollection.Name)]
public class WriteRuleTests(PostgresFixture database) : IAsyncLifetime
{
    private TenantIsolationScenario _scenario = null!;

    public async Task InitializeAsync() => _scenario = await TenantIsolationScenario.CreateAsync(database);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task NewDocumentIsStampedWithTheSelectedTenant()
    {
        var document = TestDocuments.New("new", uploadedByUserId: _scenario.Alice);

        await using (var db = database.CreateOwnerDbContext(_scenario.TenantA))
        {
            db.Documents.Add(document);
            await db.SaveChangesAsync();
        }

        Assert.Equal(_scenario.TenantA, await StoredTenantOf(document.Id));
    }

    [Fact]
    public async Task CannotInsertDocumentForAnotherTenant()
    {
        await using var db = database.CreateOwnerDbContext(_scenario.TenantA);
        db.Documents.Add(TestDocuments.New("for B", uploadedByUserId: _scenario.Alice, tenantId: _scenario.TenantB));

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task CannotInsertDocumentWithoutSelectedTenant()
    {
        await using var db = database.CreateOwnerDbContext(tenantId: null);
        db.Documents.Add(TestDocuments.New("no tenant", uploadedByUserId: _scenario.Alice, tenantId: _scenario.TenantA));

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task CannotMoveDocumentToAnotherTenant()
    {
        await using (var db = database.CreateOwnerDbContext(_scenario.TenantA))
        {
            var document = await db.Documents.SingleAsync(d => d.Id == _scenario.DocumentA1);
            document.TenantId = _scenario.TenantB;

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        Assert.Equal(_scenario.TenantA, await StoredTenantOf(_scenario.DocumentA1));
    }

    // ExecuteDelete skips SaveChanges (and so the write rules); the query filter is what
    // limits it to the current tenant.
    [Fact]
    public async Task BulkDeleteOnlyAffectsTheSelectedTenant()
    {
        await using (var db = database.CreateOwnerDbContext(_scenario.TenantA))
        {
            var deleted = await db.Documents.ExecuteDeleteAsync();

            Assert.Equal(2, deleted);
        }

        Assert.Equal(_scenario.TenantB, await StoredTenantOf(_scenario.DocumentB1));
    }

    // Reads the row as the superuser with filters off: the stored truth, not what a tenant sees.
    private async Task<Guid?> StoredTenantOf(Guid documentId)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Documents.IgnoreQueryFilters()
            .Where(d => d.Id == documentId)
            .Select(d => (Guid?)d.TenantId)
            .SingleOrDefaultAsync();
    }
}
