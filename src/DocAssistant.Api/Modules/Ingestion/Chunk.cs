using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.Shared.Tenancy;
using NpgsqlTypes;
using Pgvector;

namespace DocAssistant.Api.Modules.Ingestion;

// One searchable piece of a document (docs/decisions.md #29). Written by ingestion,
// read by search from phase 3 on.
public class Chunk : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    // The document version the chunk was made from; always 1 in phase 2 (decisions #32).
    public int DocumentVersion { get; set; }

    // Position within the document, starting at 0.
    public int Ordinal { get; set; }

    // The text that is embedded and shown as a source, starting with the context header
    // (decisions #27, #43).
    public required string Content { get; set; }

    // Null for DOCX, which has no pages (decisions #22).
    public int? PageNumber { get; set; }

    // Headings above the chunk, such as "Bölüm 3: İzinler > Yıllık İzin"; null when the
    // document has no headings before it.
    public string? SectionPath { get; set; }

    public DocumentSourceType SourceType { get; set; }

    // Both empty until phase 3 fills them (decisions #28).
    public Vector? Embedding { get; set; }

    public string? EmbeddingModel { get; set; }

    // Content prepared for keyword search (decisions #49). Computed by the database from
    // Content; the app never writes it.
    public NpgsqlTsVector SearchVector { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}
