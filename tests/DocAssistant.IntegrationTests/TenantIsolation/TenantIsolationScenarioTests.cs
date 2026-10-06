using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.TenantIsolation;

// Control: the seeded rows really exist. Without this, "tenant A cannot see B's document"
// would also pass if the document had never been written.
[Collection(DatabaseCollection.Name)]
public class TenantIsolationScenarioTests(PostgresFixture database)
{
    [Fact]
    public async Task OwnerSeesEverySeededRow()
    {
        var scenario = await TenantIsolationScenario.CreateAsync(database);
        await using var db = database.CreateOwnerDbContext();

        var documents = await db.Documents.IgnoreQueryFilters()
            .Where(d => d.TenantId == scenario.TenantA || d.TenantId == scenario.TenantB)
            .Select(d => d.Id)
            .ToListAsync();
        var carolMemberships = await db.Memberships.IgnoreQueryFilters()
            .CountAsync(m => m.UserId == scenario.Carol);

        Assert.Equivalent(new[] { scenario.DocumentA1, scenario.DocumentA2, scenario.DocumentB1 }, documents, strict: true);
        Assert.Equal(2, carolMemberships);
    }
}
