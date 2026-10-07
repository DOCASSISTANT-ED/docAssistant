namespace DocAssistant.Api.Modules.Ingestion;

// Where the upload endpoint hands a stored document to the background worker
// (docs/decisions.md #34).
public interface IIngestionQueue
{
    // The tenant travels with the document: the worker has no HTTP request to read it
    // from and must select the tenant before touching tenant data (decisions #31).
    ValueTask EnqueueAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken = default);
}
