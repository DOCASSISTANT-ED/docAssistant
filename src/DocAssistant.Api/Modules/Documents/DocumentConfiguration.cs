using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocAssistant.Api.Modules.Documents;

public class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public const int TitleMaxLength = 300;
    public const int FileNameMaxLength = 255;
    public const int ContentTypeMaxLength = 100;
    public const int StorageKeyMaxLength = 500;
    public const int FailureReasonMaxLength = 500;

    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("documents");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Title)
            .IsRequired()
            .HasMaxLength(TitleMaxLength);

        builder.Property(d => d.FileName)
            .IsRequired()
            .HasMaxLength(FileNameMaxLength);

        builder.Property(d => d.ContentType)
            .IsRequired()
            .HasMaxLength(ContentTypeMaxLength);

        builder.Property(d => d.StorageKey)
            .IsRequired()
            .HasMaxLength(StorageKeyMaxLength);

        // Two documents must never point at the same stored file.
        builder.HasIndex(d => d.StorageKey)
            .IsUnique();

        builder.Property(d => d.Version)
            .HasDefaultValue(1);

        builder.Property(d => d.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(d => d.AttemptCount)
            .HasDefaultValue(0);

        builder.Property(d => d.FailureReason)
            .HasMaxLength(FailureReasonMaxLength);

        builder.Property(d => d.CreatedAt)
            .HasDefaultValueSql("now()");

        // Restrict: a tenant that still has documents cannot be deleted by accident.
        // EF Core also creates the index on tenant_id that every filtered query relies on.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict: documents belong to the tenant, so removing the person who uploaded
        // them must not remove (or orphan) the documents.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(d => d.UploadedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
