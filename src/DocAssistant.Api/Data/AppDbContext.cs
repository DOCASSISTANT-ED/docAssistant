using DocAssistant.Api.Modules.Tenants;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Modules klasörlerindeki tüm IEntityTypeConfiguration sınıflarını toplar.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
