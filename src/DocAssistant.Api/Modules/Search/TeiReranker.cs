using System.Text.Json;

namespace DocAssistant.Api.Modules.Search;

// Reranks with a model served by Hugging Face Text Embeddings Inference (TEI)
// (docs/decisions.md #52). TEI reads the question together with each candidate's text and
// gives every candidate a relevance score.
//
// The reranker improves the order but search works without it: when the service cannot be
// reached, fails or takes too long, the candidates are returned in the order they came in
// (the fused order) and a warning is logged.
public sealed class TeiReranker(HttpClient http, ILogger<TeiReranker> logger) : IReranker
{
    // The named HttpClient carrying the service's address and timeout (SearchModule).
    public const string HttpClientName = "reranker";

    public async Task<IReadOnlyList<RankedChunk>> RerankAsync(
        string query,
        IReadOnlyList<ChunkText> candidates,
        int limit,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0 || limit <= 0)
        {
            return [];
        }

        try
        {
            // truncate: a text longer than the model's input is cut, not rejected.
            var request = new RerankRequest(query, candidates.Select(candidate => candidate.Content).ToList(), Truncate: true);

            using var response = await http.PostAsJsonAsync("rerank", request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var scores = await response.Content.ReadFromJsonAsync<List<RerankScore>>(cancellationToken);

            // One score per candidate, each pointing at its position in the request.
            if (scores is null
                || scores.Count != candidates.Count
                || scores.Select(score => score.Index).Distinct().Count() != candidates.Count
                || scores.Any(score => score.Index < 0 || score.Index >= candidates.Count))
            {
                logger.LogWarning("The reranker returned an unexpected answer; keeping the fused order.");

                return InGivenOrder(candidates, limit);
            }

            return scores
                .OrderByDescending(score => score.Score)
                .ThenBy(score => score.Index)
                .Take(limit)
                .Select(score => new RankedChunk(candidates[score.Index].ChunkId, score.Score))
                .ToList();
        }
        // HttpClient reports its own timeout as a cancellation; when the caller did not
        // cancel, that is what happened. A caller's cancellation is not caught.
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The reranker did not answer in time; keeping the fused order.");

            return InGivenOrder(candidates, limit);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "The reranker could not be used; keeping the fused order.");

            return InGivenOrder(candidates, limit);
        }
    }

    // No reranker score exists here; scores are informational only (decisions #48).
    private static List<RankedChunk> InGivenOrder(IReadOnlyList<ChunkText> candidates, int limit) =>
        candidates.Take(limit).Select(candidate => new RankedChunk(candidate.ChunkId, 0)).ToList();

    private sealed record RerankRequest(string Query, IReadOnlyList<string> Texts, bool Truncate);

    private sealed record RerankScore(int Index, double Score);
}
