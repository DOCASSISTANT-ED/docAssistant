using DocAssistant.Api.Modules.Documents;

namespace DocAssistant.Api.Modules.Ingestion;

// Temporary stand-in until K4 (file storage, chunking and the chunks table) exists.
// Leaves the document in Processing: startup recovery re-queues it and the attempt
// limit (decisions #38) eventually marks it Failed. Delete this class in K4.
public sealed class PendingDocumentProcessor(ILogger<PendingDocumentProcessor> logger) : IDocumentProcessor
{
    public Task ProcessAsync(Document document, CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Document processing is not implemented yet; document {DocumentId} stays Processing.",
            document.Id);

        return Task.CompletedTask;
    }
}
