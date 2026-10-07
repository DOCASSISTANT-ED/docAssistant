namespace DocAssistant.Api.Modules.Ingestion;

// One "process this document" message. The document's real state lives in the database;
// the queue only carries which document to look at (docs/decisions.md #34).
public sealed record IngestionWorkItem(Guid TenantId, Guid DocumentId);
