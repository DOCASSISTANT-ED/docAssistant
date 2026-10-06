using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.TenantIsolation;

// Layer 1 of tenant isolation: EF Core global query filters (docs/decisions.md #14, #15).
[Collection(DatabaseCollection.Name)]
public class QueryFilterTests(PostgresFixture database) : IAsyncLifetime
{
    private TenantIsolationScenario _scenario = null!;

    public async Task InitializeAsync() => _scenario = await TenantIsolationScenario.CreateAsync(database);

    public Task DisposeAsync() => Task.CompletedTask;

    // documents also has RLS. These tests use the superuser connection with a tenant
    // selected, so RLS is bypassed and the query filter is the only layer being tested.

    [Fact]
    public async Task TenantSeesOnlyItsOwnDocuments()
    {
        await using var db = database.CreateOwnerDbContext(_scenario.TenantA);

        var documents = await db.Documents.Select(d => d.Id).ToListAsync();

        Assert.Equivalent(new[] { _scenario.DocumentA1, _scenario.DocumentA2 }, documents);
    }

    [Fact]
    public async Task TenantCannotLoadAnotherTenantsDocumentById()
    {
        await using var db = database.CreateOwnerDbContext(_scenario.TenantA);

        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == _scenario.DocumentB1);

        Assert.Null(document);
    }

    [Fact]
    public async Task NoDocumentsWithoutSelectedTenant()
    {
        await using var db = database.CreateOwnerDbContext(tenantId: null);

        Assert.False(await db.Documents.AnyAsync());
    }

    // Identity tables have no RLS (decisions #15): the query filters are their only
    // isolation, so these use the app's own connection.

    [Fact]
    public async Task TenantSeesOnlyItself()
    {
        await using var db = database.CreateAppDbContext(_scenario.TenantA);

        var tenants = await db.Tenants.Select(t => t.Id).ToListAsync();

        Assert.Equal([_scenario.TenantA], tenants);
    }

    [Fact]
    public async Task TenantSeesOnlyItsOwnMemberships()
    {
        await using var db = database.CreateAppDbContext(_scenario.TenantA);

        var memberships = await db.Memberships.ToListAsync();

        Assert.NotEmpty(memberships);
        Assert.All(memberships, m => Assert.Equal(_scenario.TenantA, m.TenantId));
    }

    [Fact]
    public async Task TenantSeesOnlyUsersWhoAreItsMembers()
    {
        await using var db = database.CreateAppDbContext(_scenario.TenantA);

        var users = await db.Users.Select(u => u.Id).ToListAsync();

        Assert.Equivalent(new[] { _scenario.Alice, _scenario.Carol }, users);
    }

    [Fact]
    public async Task UserInTwoTenantsIsVisibleFromBothButOnlyWithTheCurrentTenantsMembership()
    {
        await using var fromA = database.CreateAppDbContext(_scenario.TenantA);
        await using var fromB = database.CreateAppDbContext(_scenario.TenantB);

        var carolFromA = await fromA.Users.Include(u => u.Memberships).SingleAsync(u => u.Id == _scenario.Carol);
        var carolFromB = await fromB.Users.Include(u => u.Memberships).SingleAsync(u => u.Id == _scenario.Carol);

        Assert.Equal([_scenario.CarolMembershipA], carolFromA.Memberships.Select(m => m.Id));
        Assert.Equal([_scenario.CarolMembershipB], carolFromB.Memberships.Select(m => m.Id));
    }

    [Fact]
    public async Task NoIdentityDataWithoutSelectedTenant()
    {
        await using var db = database.CreateAppDbContext(tenantId: null);

        Assert.False(await db.Tenants.AnyAsync());
        Assert.False(await db.Memberships.AnyAsync());
        Assert.False(await db.Users.AnyAsync());
    }
}
