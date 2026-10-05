using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocAssistant.Api.Data;

// Used only by the `dotnet ef` tools. Migrations create tables and RLS policies, so they
// connect with the "Migrations" connection string (the database owner). The running app
// uses "Default" (the restricted docassistant_app user). See docs/decisions.md #19.
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("Migrations")
            ?? throw new InvalidOperationException("Connection string 'Migrations' is not configured.");

        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseAppDatabase(connectionString);

        return new AppDbContext(options.Options, new NoTenantContext());
    }

    private sealed class NoTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
    }
}
