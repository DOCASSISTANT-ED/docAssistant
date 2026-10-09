namespace DocAssistant.Api.Modules.Search;

// Meaning-based search over chunk embeddings (bge-m3, pgvector; docs/decisions.md #8,
// #48). Finds chunks that say the same thing in other words ("senelik tatil" for
// "yıllık izin"). Embedding the query is part of the search.
//
// Runs inside a request: the query filter and RLS already limit it to the caller's
// tenant, so there is no tenant parameter. Chunks without an embedding are not found.
public interface IVectorChunkSearch
{
    // At most limit chunks, most similar first.
    Task<IReadOnlyList<RankedChunk>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}
