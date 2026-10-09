namespace DocAssistant.Api.Modules.Search;

// Re-scores the fused candidates against the question with a reranker model and keeps the
// best (docs/decisions.md #48). Needs the chunks' text, which the searches do not carry.
//
// Optional until a reranker model is chosen: without one, retrieval keeps the fused order.
public interface IReranker
{
    // At most limit of the candidates, best first.
    Task<IReadOnlyList<RankedChunk>> RerankAsync(
        string query,
        IReadOnlyList<ChunkText> candidates,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record ChunkText(Guid ChunkId, string Content);
