using DocAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.Api.Modules.Ingestion;

// Runs once at startup: the in-memory queue is empty after a restart, so documents left
// Pending or Processing are put back on it (docs/decisions.md #34).
//
// A BackgroundService does not hold up startup. That matters: with more unfinished
// documents than the queue holds (#39), enqueueing waits for the worker, which can only
// run once startup has finished.
public sealed class IngestionRecoveryService(
    IServiceScopeFactory scopeFactory,
    IIngestionQueue queue,
    ILogger<IngestionRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var unfinished = await FindUnfinishedDocumentsAsync(stoppingToken);

            foreach (var document in unfinished)
            {
                await queue.EnqueueAsync(document.TenantId, document.DocumentId, stoppingToken);
            }

            logger.LogInformation("Re-queued {Count} unfinished document(s) for ingestion.", unfinished.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App is shutting down; whatever was not re-queued is still Pending in the
            // database and will be picked up on the next start.
        }
        catch (Exception ex)
        {
            // Do not take the whole API down: documents stay Pending/Processing in the
            // database and the next start tries again.
            logger.LogError(ex, "Could not re-queue unfinished documents.");
        }
    }

    private async Task<List<IngestionWorkItem>> FindUnfinishedDocumentsAsync(CancellationToken cancellationToken)
    {
        // AppDbContext is scoped (one per request); outside a request we make our own scope.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // No tenant is selected here, so RLS hides every document. The function returns
        // only ids, across all tenants (migration AddIngestionRecoveryFunction).
        return await db.Database
            .SqlQuery<IngestionWorkItem>($"SELECT tenant_id, document_id FROM unfinished_documents()")
            .ToListAsync(cancellationToken);
    }
}
