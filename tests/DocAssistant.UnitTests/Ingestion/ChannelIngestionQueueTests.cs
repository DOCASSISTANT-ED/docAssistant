using DocAssistant.Api.Modules.Ingestion;

namespace DocAssistant.UnitTests.Ingestion;

// The in-memory queue between the upload endpoint and the worker (docs/decisions.md #34, #39).
public class ChannelIngestionQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task MessagesComeOutInTheOrderTheyWentIn()
    {
        var queue = new ChannelIngestionQueue();
        var first = new IngestionWorkItem(Guid.NewGuid(), Guid.NewGuid());
        var second = new IngestionWorkItem(Guid.NewGuid(), Guid.NewGuid());

        await queue.EnqueueAsync(first.TenantId, first.DocumentId);
        await queue.EnqueueAsync(second.TenantId, second.DocumentId);

        Assert.Equal([first, second], await ReadAsync(queue, count: 2));
    }

    // decisions #31: the worker has no request to read the tenant from.
    [Fact]
    public async Task MessageCarriesTheTenantTogetherWithTheDocument()
    {
        var queue = new ChannelIngestionQueue();
        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        await queue.EnqueueAsync(tenantId, documentId);

        var item = Assert.Single(await ReadAsync(queue, count: 1));
        Assert.Equal(tenantId, item.TenantId);
        Assert.Equal(documentId, item.DocumentId);
    }

    [Fact]
    public async Task ReaderWaitsWhileTheQueueIsEmptyAndGetsLaterMessages()
    {
        var queue = new ChannelIngestionQueue();
        var reading = ReadAsync(queue, count: 1);

        await Task.Delay(100);
        Assert.False(reading.IsCompleted);

        var documentId = Guid.NewGuid();
        await queue.EnqueueAsync(Guid.NewGuid(), documentId);

        var item = Assert.Single(await reading.WaitAsync(Timeout));
        Assert.Equal(documentId, item.DocumentId);
    }

    // App shutdown: the worker's read loop must end instead of waiting forever.
    [Fact]
    public async Task ReadingStopsWhenCancelled()
    {
        var queue = new ChannelIngestionQueue();
        using var shutdown = new CancellationTokenSource();

        var reading = Task.Run(async () =>
        {
            await foreach (var _ in queue.ReadAllAsync(shutdown.Token))
            {
            }
        });

        shutdown.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(Timeout));
    }

    // decisions #39: at most 100 waiting messages.
    [Fact]
    public async Task AcceptsMessagesUpToItsCapacityWithoutAReader()
    {
        var queue = new ChannelIngestionQueue();

        for (var i = 0; i < ChannelIngestionQueue.Capacity; i++)
        {
            await queue.EnqueueAsync(Guid.NewGuid(), Guid.NewGuid()).AsTask().WaitAsync(Timeout);
        }

        Assert.Equal(100, ChannelIngestionQueue.Capacity);
    }

    // decisions #39: a full queue makes the writer wait instead of growing or dropping.
    [Fact]
    public async Task WriterWaitsWhenTheQueueIsFullAndContinuesOnceThereIsRoom()
    {
        var queue = new ChannelIngestionQueue();
        await FillAsync(queue);
        var extra = Guid.NewGuid();

        var writing = queue.EnqueueAsync(Guid.NewGuid(), extra).AsTask();

        await Task.Delay(100);
        Assert.False(writing.IsCompleted);

        var all = await ReadAsync(queue, count: ChannelIngestionQueue.Capacity + 1);

        await writing.WaitAsync(Timeout);
        Assert.Equal(extra, all[^1].DocumentId);
    }

    // An upload waiting on a full queue must be able to give up (client disconnects).
    [Fact]
    public async Task WaitingWriterCanBeCancelled()
    {
        var queue = new ChannelIngestionQueue();
        await FillAsync(queue);
        using var requestAborted = new CancellationTokenSource();

        var writing = queue.EnqueueAsync(Guid.NewGuid(), Guid.NewGuid(), requestAborted.Token).AsTask();
        requestAborted.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(Timeout));
    }

    private static async Task FillAsync(ChannelIngestionQueue queue)
    {
        for (var i = 0; i < ChannelIngestionQueue.Capacity; i++)
        {
            await queue.EnqueueAsync(Guid.NewGuid(), Guid.NewGuid());
        }
    }

    private static async Task<List<IngestionWorkItem>> ReadAsync(ChannelIngestionQueue queue, int count)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var items = new List<IngestionWorkItem>();

        await foreach (var item in queue.ReadAllAsync(timeout.Token))
        {
            items.Add(item);

            if (items.Count == count)
            {
                break;
            }
        }

        return items;
    }
}
