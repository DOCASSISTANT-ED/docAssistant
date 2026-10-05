using DocAssistant.Api.Modules.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocAssistant.Api.Modules.Documents;

public class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public const int TitleMaxLength = 300;

    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("documents");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Title)
            .IsRequired()
            .HasMaxLength(TitleMaxLength);

        builder.Property(d => d.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(d => d.CreatedAt)
            .HasDefaultValueSql("now()");

        // Restrict: a tenant that still has documents cannot be deleted by accident.
        // EF Core also creates the index on tenant_id that every filtered query relies on.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
