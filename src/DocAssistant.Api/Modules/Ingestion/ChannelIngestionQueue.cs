using System.Threading.Channels;

namespace DocAssistant.Api.Modules.Ingestion;

// In-memory queue between the upload endpoint (writes) and the background worker (reads).
// Messages are lost when the app stops; startup recovery re-enqueues unfinished documents
// from the database (docs/decisions.md #34).
public sealed class ChannelIngestionQueue : IIngestionQueue
{
    // decisions #39: at most 100 waiting messages; when full, writers wait for room
    // instead of growing memory without limit.
    public const int Capacity = 100;

    private readonly Channel<IngestionWorkItem> _channel = Channel.CreateBounded<IngestionWorkItem>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true, // decisions #39: one worker, documents processed one at a time
        });

    public ValueTask EnqueueAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(new IngestionWorkItem(tenantId, documentId), cancellationToken);

    // For the background worker: yields messages in arrival order, waiting while the queue
    // is empty, until cancellationToken fires (app shutdown).
    public IAsyncEnumerable<IngestionWorkItem> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
