namespace DocAssistant.Api.Modules.Ingestion.Chunking;

// A chunk before it belongs to a stored document: the processor adds ids, tenant,
// version and source type when it turns drafts into Chunk rows.
//
// Content starts with the context header ("Belge: ... > ..."), followed by a blank line
// and the chunk's text (docs/decisions.md #27, #43).
public sealed record ChunkDraft(int Ordinal, string Content, int? PageNumber, string? SectionPath);
