namespace DocAssistant.Api.Modules.Search;

// Merges the rankings of several searches into one (Reciprocal Rank Fusion, docs/decisions.md
// #48). Uses only each chunk's position in each ranking, never the scores: the searches
// score on different scales.
public interface IRankFusion
{
    // At most limit chunks, best first. A chunk found by several searches appears once.
    IReadOnlyList<RankedChunk> Fuse(IReadOnlyList<IReadOnlyList<RankedChunk>> rankings, int limit);
}
