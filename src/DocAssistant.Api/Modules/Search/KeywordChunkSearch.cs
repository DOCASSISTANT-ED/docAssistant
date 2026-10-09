using DocAssistant.Api.Data;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.Api.Modules.Search;

// Full-text search over chunks.search_vector (docs/decisions.md #49).
//
// The question is turned into search terms by PostgreSQL itself, with the same casing
// fix and turkish configuration as the stored column (ChunkConfiguration.SearchVectorSql),
// so both sides are always processed alike. plainto_tsquery joins the terms with AND
// ("&"); a chunk must then contain every word of the question, including words such as
// "kaç" that never appear in documents. Replacing "&" with "|" (OR) lets any word match,
// and ts_rank_cd puts chunks matching more of them, closer together, first.
//
// Raw SQL bypasses EF Core's query filter, so the tenant condition is written out here;
// RLS still applies underneath (decisions #14, #15).
public sealed class KeywordChunkSearch(AppDbContext db, ITenantContext tenantContext) : IKeywordChunkSearch
{
    public async Task<IReadOnlyList<RankedChunk>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        // Without a tenant there is nothing the caller may see.
        if (tenantContext.TenantId is not { } tenantId || string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }

        // Interpolated values become SQL parameters; nothing from the question is pasted
        // into the SQL text.
        return await db.Database
            .SqlQuery<RankedChunk>(
                $"""
                SELECT c.id AS chunk_id, ts_rank_cd(c.search_vector, q.terms)::double precision AS score
                FROM chunks AS c,
                     (SELECT replace(plainto_tsquery('turkish', translate({query}, 'Iİ', 'ıi'))::text, '&', '|')::tsquery AS terms) AS q
                WHERE c.tenant_id = {tenantId}
                  AND c.search_vector @@ q.terms
                ORDER BY score DESC, c.id
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken);
    }
}
