using System.Reflection;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Tenants;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<Document> Documents => Set<Document>();

    // Query filters must read the tenant through a member of the context: EF Core then
    // re-evaluates it for every query instead of baking one value into the cached model.
    private Guid? CurrentTenantId => tenantContext.TenantId;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyTenantWriteRules();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyTenantWriteRules();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Picks up every IEntityTypeConfiguration in the Modules folders.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // docs/decisions.md #14: every ITenantOwned table is filtered to the current tenant.
        var applyTenantFilter = typeof(AppDbContext)
            .GetMethod(nameof(ApplyTenantFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                applyTenantFilter.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }
        }
    }

    // When no tenant is selected CurrentTenantId is null and the filter matches no rows.
    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantOwned
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity => entity.TenantId == CurrentTenantId);
    }

    private void ApplyTenantWriteRules()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            var entityName = entry.Metadata.ClrType.Name;

            if (entry.State == EntityState.Added)
            {
                var tenantId = CurrentTenantId
                    ?? throw new InvalidOperationException(
                        $"Cannot insert {entityName}: no tenant is selected for this operation.");

                if (entry.Entity.TenantId == Guid.Empty)
                {
                    entry.Entity.TenantId = tenantId;
                }
                else if (entry.Entity.TenantId != tenantId)
                {
                    throw new InvalidOperationException(
                        $"Cannot insert {entityName} for a tenant other than the selected one.");
                }
            }
            else if (entry.State == EntityState.Modified
                && entry.Property(nameof(ITenantOwned.TenantId)).IsModified)
            {
                throw new InvalidOperationException(
                    $"Cannot move {entityName} to another tenant.");
            }
        }
    }
}
