using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocAssistant.IntegrationTests.Ingestion;

internal static class IngestionTestSupport
{
    // The shared test API runs the real recovery service and worker. Recovery re-queues
    // every unfinished document it finds when the app starts, and the worker then touches
    // them (status, attempt count). The app starts lazily, so without this it could start
    // in the middle of a test and pick up that test's documents. Call it before creating
    // any: documents created after recovery has finished never reach the shared queue.
    public static async Task WaitForStartupRecoveryAsync(PostgresFixture database)
    {
        var recovery = database.Api.Services
            .GetServices<IHostedService>()
            .OfType<IngestionRecoveryService>()
            .Single();

        await (recovery.ExecuteTask ?? Task.CompletedTask);
    }

    // Puts a document into a given state directly, as the superuser: the tests are about
    // what ingestion does next, not about how the document got there.
    public static async Task SetStateAsync(
        PostgresFixture database,
        Guid documentId,
        DocumentStatus status,
        int attemptCount = 0)
    {
        await using var db = database.CreateOwnerDbContext();

        var updated = await db.Documents.IgnoreQueryFilters()
            .Where(d => d.Id == documentId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(d => d.Status, status)
                .SetProperty(d => d.AttemptCount, attemptCount));

        Assert.Equal(1, updated);
    }

    // The stored truth, read as the superuser with filters off.
    public static async Task<Document> ReadAsync(PostgresFixture database, Guid documentId)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Documents.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == documentId);
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Timed out waiting: {because}");
    }
}

// Collects what recovery hands to the queue instead of feeding a real worker.
internal sealed class RecordingQueue : IIngestionQueue
{
    private readonly List<IngestionWorkItem> _items = [];

    public IReadOnlyList<IngestionWorkItem> Items => _items;

    public ValueTask EnqueueAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken = default)
    {
        _items.Add(new IngestionWorkItem(tenantId, documentId));
        return ValueTask.CompletedTask;
    }
}

internal sealed class BrokenQueue : IIngestionQueue
{
    public ValueTask EnqueueAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The queue is broken.");
}
