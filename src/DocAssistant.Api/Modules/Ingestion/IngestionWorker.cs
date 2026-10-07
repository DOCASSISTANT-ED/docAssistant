namespace DocAssistant.Api.Modules.Ingestion;

// Reads the ingestion queue for the lifetime of the app and processes documents one at a
// time (docs/decisions.md #39). The per-document work is in DocumentIngestionHandler.
public sealed class IngestionWorker(
    ChannelIngestionQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in queue.ReadAllAsync(stoppingToken))
            {
                await HandleAsync(item, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App is shutting down. A document cut off mid-processing stays Processing in
            // the database; startup recovery re-queues it on the next start.
        }
    }

    private async Task HandleAsync(IngestionWorkItem item, CancellationToken stoppingToken)
    {
        try
        {
            // A new scope per document: one tenant and one AppDbContext each (decisions #31).
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<DocumentIngestionHandler>();

            await handler.HandleAsync(item, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An unhandled exception in a BackgroundService stops the whole app; one bad
            // document must not do that. The document stays Processing and is retried on
            // the next start, up to the attempt limit (decisions #38).
            logger.LogError(ex, "Ingestion of document {DocumentId} failed unexpectedly.", item.DocumentId);
        }
    }
}
