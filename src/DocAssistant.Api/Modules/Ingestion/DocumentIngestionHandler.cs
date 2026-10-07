using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.Api.Modules.Ingestion;

// Handles one queue message. Scoped: the worker resolves it from a new scope per message,
// because a scope can hold only one tenant (decisions #31).
//
// Exceptions other than DocumentParseException are not caught here: the document stays
// Processing, startup recovery re-queues it, and the attempt limit (#38) stops the loop.
public sealed class DocumentIngestionHandler(
    AppDbContext db,
    ITenantSelector tenantSelector,
    IDocumentProcessor processor,
    ILogger<DocumentIngestionHandler> logger)
{
    // decisions #38
    public const int MaxAttempts = 3;

    public const string TooManyAttemptsMessage =
        "Belge birkaç denemede işlenemedi. Lütfen yeniden yükleyin; sorun sürerse yöneticinize bildirin.";

    public async Task HandleAsync(IngestionWorkItem item, CancellationToken cancellationToken)
    {
        // Before any database access: query filters, the write check and RLS all read
        // the tenant from this scope.
        tenantSelector.Select(item.TenantId);

        var document = await db.Documents
            .SingleOrDefaultAsync(d => d.Id == item.DocumentId, cancellationToken);

        if (document is null)
        {
            logger.LogWarning("Document {DocumentId} not found; skipping.", item.DocumentId);
            return;
        }

        // decisions #36: the same document can arrive from both upload and recovery.
        if (document.Status is DocumentStatus.Done or DocumentStatus.Failed)
        {
            logger.LogInformation(
                "Document {DocumentId} is already {Status}; skipping.", document.Id, document.Status);
            return;
        }

        if (document.AttemptCount >= MaxAttempts)
        {
            logger.LogWarning(
                "Document {DocumentId} reached {MaxAttempts} attempts; marking it Failed.", document.Id, MaxAttempts);
            await MarkFailedAsync(document.Id, TooManyAttemptsMessage, cancellationToken);
            return;
        }

        // Saved before processing starts, so an attempt that crashes the app still counts.
        document.AttemptCount++;
        document.Status = DocumentStatus.Processing;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await processor.ProcessAsync(document, cancellationToken);
        }
        catch (DocumentParseException ex)
        {
            // decisions #24: the message is written for the user.
            logger.LogInformation("Document {DocumentId} could not be parsed: {Reason}", document.Id, ex.Message);
            await MarkFailedAsync(document.Id, ex.Message, cancellationToken);
        }
    }

    private async Task MarkFailedAsync(Guid documentId, string reason, CancellationToken cancellationToken)
    {
        // The processor may have left unsaved changes behind (such as half-built chunks);
        // forget them so only the status change below is written.
        db.ChangeTracker.Clear();

        var document = await db.Documents.SingleAsync(d => d.Id == documentId, cancellationToken);
        document.Status = DocumentStatus.Failed;
        document.FailureReason = reason;
        await db.SaveChangesAsync(cancellationToken);
    }
}
