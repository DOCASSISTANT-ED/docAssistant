using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace DocAssistant.Api.Data;

public static class AppDbContextOptions
{
    // Shared by the running app and the design-time factory, so migrations are always
    // generated with the same provider settings the app runs with.
    public static DbContextOptionsBuilder UseAppDatabase(
        this DbContextOptionsBuilder options,
        string connectionString)
    {
        return options
            // UseVector: maps Pgvector's Vector type to the vector column type.
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention();
    }
}
