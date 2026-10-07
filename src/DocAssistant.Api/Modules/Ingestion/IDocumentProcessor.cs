using DocAssistant.Api.Modules.Documents;

namespace DocAssistant.Api.Modules.Ingestion;

// The actual work on one document: read the file, parse, chunk, store. The worker handles
// everything around it (tenant, duplicate messages #36, attempt limit #38, failures #24).
//
// Called inside the worker's per-document scope, with the tenant already selected and the
// document loaded and tracked by that scope's AppDbContext, in status Processing.
public interface IDocumentProcessor
{
    // On success, sets the document to Done (and ProcessedAt) in the same transaction as
    // its chunks (decisions #35). Throws DocumentParseException when the file cannot be
    // used; the worker then marks the document Failed with the exception's message.
    Task ProcessAsync(Document document, CancellationToken cancellationToken);
}
