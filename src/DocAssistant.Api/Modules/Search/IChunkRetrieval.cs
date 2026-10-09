namespace DocAssistant.Api.Modules.Search;

// The one entry point to search (docs/decisions.md #48): keyword and vector search, fusion,
// loading the candidates' text, then reranking. Chat (phase 4) and the evaluation run
// call only this.
//
// Runs inside a request, so results come from the caller's tenant only.
public interface IChunkRetrieval
{
    // The best chunks for the question, best first; at most SearchOptions.Results.
    Task<IReadOnlyList<SearchResult>> RetrieveAsync(string query, CancellationToken cancellationToken);
}

// What chat needs to quote a source and what evaluation needs to match evidence (#47).
// Score is the last step's score (reranker, or fusion when there is no reranker); for
// reports only.
public sealed record SearchResult(
    Guid ChunkId,
    Guid DocumentId,
    string DocumentTitle,
    string? SectionPath,
    int? PageNumber,
    string Content,
    double Score);
