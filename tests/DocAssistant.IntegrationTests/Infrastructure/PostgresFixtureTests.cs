using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    // Fails if the init script did not run and docassistant_app was never created.
    [Fact]
    public async Task AppUserCanConnect()
    {
        var currentUser = await QueryScalarAsync<string>(database.AppConnectionString, "SELECT current_user");

        Assert.Equal(PostgresFixture.AppUser, currentUser);
    }

    // Superusers and BYPASSRLS roles ignore every policy; if the app user ever became one,
    // tenant isolation would silently stop working (decisions #15, #19).
    [Fact]
    public async Task AppUserIsNotSuperuserAndCannotBypassRowLevelSecurity()
    {
        var isSuperuser = await QueryScalarAsync<bool>(
            database.SuperuserConnectionString,
            $"SELECT rolsuper FROM pg_roles WHERE rolname = '{PostgresFixture.AppUser}'");
        var bypassesRls = await QueryScalarAsync<bool>(
            database.SuperuserConnectionString,
            $"SELECT rolbypassrls FROM pg_roles WHERE rolname = '{PostgresFixture.AppUser}'");

        Assert.False(isSuperuser);
        Assert.False(bypassesRls);
    }

    // Covers every table, including ones added later, so a missing GRANT is caught here
    // instead of as "permission denied" at runtime.
    [Fact]
    public async Task AppUserCanReadAndWriteEveryApplicationTable()
    {
        var tablesWithoutAccess = await QueryListAsync(
            database.SuperuserConnectionString,
            $"""
            SELECT tablename
            FROM pg_tables
            WHERE schemaname = 'public'
              AND tablename <> '__EFMigrationsHistory'
              AND NOT has_table_privilege('{PostgresFixture.AppUser}', format('%I.%I', schemaname, tablename), 'SELECT, INSERT, UPDATE, DELETE')
            ORDER BY tablename
            """);

        Assert.Empty(tablesWithoutAccess);
    }

    private static async Task<T> QueryScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> QueryListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
