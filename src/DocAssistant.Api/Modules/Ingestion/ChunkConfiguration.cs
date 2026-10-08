using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocAssistant.Api.Modules.Ingestion;

public class ChunkConfiguration : IEntityTypeConfiguration<Chunk>
{
    // bge-m3 (decisions #8).
    public const int EmbeddingDimensions = 1024;

    public const int EmbeddingModelMaxLength = 100;

    public void Configure(EntityTypeBuilder<Chunk> builder)
    {
        builder.ToTable("chunks");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Content)
            .IsRequired();

        builder.Property(c => c.SourceType)
            .HasConversion<string>()
            .HasMaxLength(10);

        builder.Property(c => c.Embedding)
            .HasColumnType($"vector({EmbeddingDimensions})");

        builder.Property(c => c.EmbeddingModel)
            .HasMaxLength(EmbeddingModelMaxLength);

        builder.Property(c => c.CreatedAt)
            .HasDefaultValueSql("now()");

        // A document's chunks are one ordered set (decisions #35); the index also serves
        // "delete this document's chunks" and "read them in order".
        builder.HasIndex(c => new { c.DocumentId, c.Ordinal })
            .IsUnique();

        // Cascade: chunks mean nothing without their document (decisions #29).
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(c => c.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, as for documents: a tenant with data cannot be deleted by accident.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
