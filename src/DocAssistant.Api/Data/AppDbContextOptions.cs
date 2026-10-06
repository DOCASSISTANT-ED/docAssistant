using Microsoft.EntityFrameworkCore;

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
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();
    }
}
