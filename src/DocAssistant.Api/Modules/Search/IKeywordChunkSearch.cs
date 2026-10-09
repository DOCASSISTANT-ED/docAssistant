namespace DocAssistant.Api.Modules.Search;

// Full-text search over chunks with PostgreSQL's turkish configuration (docs/decisions.md
// #48). Finds exact terms well ("1.500 TL", "Md."), even when the meaning-based search
// does not.
//
// Runs inside a request: the query filter and RLS already limit it to the caller's
// tenant, so there is no tenant parameter.
public interface IKeywordChunkSearch
{
    // At most limit chunks, best match first; an empty list when nothing matches or the
    // query has no searchable words.
    Task<IReadOnlyList<RankedChunk>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}
