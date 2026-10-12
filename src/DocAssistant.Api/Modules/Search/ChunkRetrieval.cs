using DocAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocAssistant.Api.Modules.Search;

// The search pipeline (docs/decisions.md #48): keyword and vector search with the same
// question, fusion of the two rankings, the candidates' text loaded once, then the
// reranker when there is one.
//
// The two searches run one after the other: they share the request's AppDbContext, which
// can do only one thing at a time.
public sealed class ChunkRetrieval(
    IKeywordChunkSearch keywordSearch,
    IVectorChunkSearch vectorSearch,
    IRankFusion fusion,
    AppDbContext db,
    IOptions<SearchOptions> options,
    IReranker? reranker = null) : IChunkRetrieval
{
    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var settings = options.Value;

        var byKeyword = await keywordSearch.SearchAsync(query, settings.CandidatesPerSearch, cancellationToken);
        var byVector = await vectorSearch.SearchAsync(query, settings.CandidatesPerSearch, cancellationToken);

        // Without a reranker the fused order is final, so only the results are needed.
        var candidateCount = reranker is null ? settings.Results : settings.CandidatesForReranking;
        var candidates = fusion.Fuse([byKeyword, byVector], candidateCount);

        if (candidates.Count == 0)
        {
            return [];
        }

        // Text and source details are loaded here, once, for the candidates only; the
        // searches carry ids. The query filter keeps this to the caller's tenant too.
        var candidateIds = candidates.Select(candidate => candidate.ChunkId).ToList();

        var chunks = await (
            from chunk in db.Chunks.AsNoTracking()
            join document in db.Documents.AsNoTracking() on chunk.DocumentId equals document.Id
            where candidateIds.Contains(chunk.Id)
            select new
            {
                chunk.Id,
                chunk.DocumentId,
                DocumentTitle = document.Title,
                chunk.SectionPath,
                chunk.PageNumber,
                chunk.Content,
            })
            .ToDictionaryAsync(chunk => chunk.Id, cancellationToken);

        // A chunk can disappear between the search and this point (its document was
        // processed again or deleted); such a candidate is dropped.
        var found = candidates.Where(candidate => chunks.ContainsKey(candidate.ChunkId)).ToList();

        var ranked = reranker is null
            ? found
            : await reranker.RerankAsync(
                query,
                found.Select(candidate => new ChunkText(candidate.ChunkId, chunks[candidate.ChunkId].Content)).ToList(),
                settings.Results,
                cancellationToken);

        return ranked
            .Take(settings.Results)
            .Select(result =>
            {
                var chunk = chunks[result.ChunkId];

                return new SearchResult(
                    chunk.Id,
                    chunk.DocumentId,
                    chunk.DocumentTitle,
                    chunk.SectionPath,
                    chunk.PageNumber,
                    chunk.Content,
                    result.Score);
            })
            .ToList();
    }
}
