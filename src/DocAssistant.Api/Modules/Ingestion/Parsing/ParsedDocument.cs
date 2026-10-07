namespace DocAssistant.Api.Modules.Ingestion.Parsing;

// What a parser returns. The document's name is not part of it: that is known at upload
// time and stored on the document row.
public sealed record ParsedDocument(IReadOnlyList<DocumentBlock> Blocks);
