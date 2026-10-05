using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocAssistant.Api.Modules.Tenants;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public const int NameMaxLength = 200;

    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name)
            .IsRequired()
            .HasMaxLength(NameMaxLength);

        builder.Property(t => t.CreatedAt)
            .HasDefaultValueSql("now()");
    }
}
