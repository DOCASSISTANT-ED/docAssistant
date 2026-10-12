using DocAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocAssistant.Api.Modules.Embeddings;

// Runs once at startup (docs/decisions.md #54): chunks without an embedding (stored before
// embeddings existed, #28) or embedded with another model than the configured one are
// embedded again, document by document. Vector search leaves such chunks out (#51), so
// until this has run they can only be found by keyword search.
//
// Changing the embedding model is therefore: change the setting, restart the app. With
// nothing outdated, this does one query and ends.
//
// A BackgroundService, so startup and requests do not wait for it.
public sealed class ReindexService(
    IServiceScopeFactory scopeFactory,
    IOptions<EmbeddingOptions> embeddingOptions,
    ILogger<ReindexService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!embeddingOptions.Value.ReindexOnStartup)
        {
            return;
        }

        try
        {
            var documents = await FindDocumentsAsync(stoppingToken);

            if (documents.Count == 0)
            {
                return;
            }

            logger.LogInformation(
                "Re-indexing {Count} document(s) with the embedding model '{Model}'.",
                documents.Count,
                embeddingOptions.Value.Model);

            var done = 0;

            foreach (var document in documents)
            {
                if (!await ReindexAsync(document, stoppingToken))
                {
                    logger.LogWarning(
                        "Re-indexing stopped after {Done} of {Count} document(s); the next start continues.",
                        done,
                        documents.Count);
                    return;
                }

                done++;
            }

            logger.LogInformation("Re-indexing finished: {Count} document(s).", documents.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App is shutting down; chunks not reached yet are still outdated in the
            // database and will be found on the next start.
        }
        catch (Exception ex)
        {
            // Do not take the whole API down: keyword search still works, and the next
            // start tries again.
            logger.LogError(ex, "Could not look for chunks to re-index.");
        }
    }

    // False when re-indexing should stop: the model cannot be used, so the documents
    // after this one would fail the same way.
    private async Task<bool> ReindexAsync(ReindexItem document, CancellationToken cancellationToken)
    {
        // A scope per document: its own AppDbContext and its own tenant (decisions #31).
        await using var scope = scopeFactory.CreateAsyncScope();
        var reindexer = scope.ServiceProvider.GetRequiredService<ChunkReindexer>();

        try
        {
            var count = await reindexer.ReindexAsync(document, cancellationToken);

            logger.LogInformation(
                "Re-indexed {Count} chunk(s) of document {DocumentId}.", count, document.DocumentId);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DbUpdateException ex)
        {
            // This document's chunks could not be written, for example because it was
            // processed again meanwhile and its old chunks are gone. Others may be fine.
            logger.LogWarning(ex, "Could not re-index document {DocumentId}; skipping it.", document.DocumentId);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The embedding model could not be used for document {DocumentId}.", document.DocumentId);

            return false;
        }
    }

    private async Task<List<ReindexItem>> FindDocumentsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var model = embeddingOptions.Value.Model;

        // No tenant is selected here, so RLS hides every chunk. The function returns only
        // ids, across all tenants (migration AddReindexFunction).
        return await db.Database
            .SqlQuery<ReindexItem>($"SELECT tenant_id, document_id FROM documents_to_reindex({model})")
            .ToListAsync(cancellationToken);
    }
}
