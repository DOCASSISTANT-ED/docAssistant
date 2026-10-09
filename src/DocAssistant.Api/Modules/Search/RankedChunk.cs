namespace DocAssistant.Api.Modules.Search;

// One entry of a ranking produced by a search step (docs/decisions.md #48). The position
// in the list is the rank: the first entry is the best match.
//
// Score is for logs and evaluation reports only. Each step computes it on its own scale
// (full-text rank, vector similarity, fusion score, reranker score), so scores of different
// steps are never compared or combined; fusion works on positions, not scores.
public sealed record RankedChunk(Guid ChunkId, double Score);
