using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Infrastructure;

// Checks that the test database itself is built correctly; feature tests rely on it.
[Collection(DatabaseCollection.Name)]
public class PostgresFixtureTests(PostgresFixture database)
{
    [Fact]
    public async Task AllMigrationsAreApplied()
    {
        await using var db = database.CreateOwnerDbContext();

        var defined = db.Database.GetMigrations();
        var applied = await db.Database.GetAppliedMigrationsAsync();

        Assert.NotEmpty(defined);
        Assert.Equal(defined, applied);
    }
}
