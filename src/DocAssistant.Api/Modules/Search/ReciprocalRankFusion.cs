namespace DocAssistant.Api.Modules.Search;

// Reciprocal Rank Fusion (docs/decisions.md #48): every ranking gives a chunk
// 1 / (RankConstant + position), position counted from 1, and the chunk's fused score is
// the sum over the rankings that found it. A chunk both searches put near the top beats
// one that only a single search ranked first.
//
// Only positions are used. The searches score on different scales (full-text rank, vector
// similarity), so their scores cannot be added or compared.
public sealed class ReciprocalRankFusion : IRankFusion
{
    // The value from the paper that introduced RRF (Cormack, Clarke and Büttcher, 2009)
    // and the usual default. A larger constant flattens the difference between positions,
    // so agreement between the searches counts for more than a single first place.
    public const int RankConstant = 60;

    public IReadOnlyList<RankedChunk> Fuse(IReadOnlyList<IReadOnlyList<RankedChunk>> rankings, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        var fused = new Dictionary<Guid, Entry>();
        var seenInRanking = new HashSet<Guid>();

        foreach (var ranking in rankings)
        {
            seenInRanking.Clear();
            var position = 0;

            foreach (var chunk in ranking)
            {
                // A chunk listed twice by one search counts once, at its best position.
                if (!seenInRanking.Add(chunk.ChunkId))
                {
                    continue;
                }

                position++;
                var score = 1.0 / (RankConstant + position);

                fused[chunk.ChunkId] = fused.TryGetValue(chunk.ChunkId, out var entry)
                    ? entry with { Score = entry.Score + score, BestPosition = Math.Min(entry.BestPosition, position) }
                    : new Entry(chunk.ChunkId, score, position, fused.Count);
            }
        }

        // Equal scores (two chunks each found once, at the same position) must not depend
        // on dictionary order: the better single position wins, then the chunk met first.
        return fused.Values
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.BestPosition)
            .ThenBy(entry => entry.FirstSeen)
            .Take(limit)
            .Select(entry => new RankedChunk(entry.ChunkId, entry.Score))
            .ToList();
    }

    private readonly record struct Entry(Guid ChunkId, double Score, int BestPosition, int FirstSeen);
}
