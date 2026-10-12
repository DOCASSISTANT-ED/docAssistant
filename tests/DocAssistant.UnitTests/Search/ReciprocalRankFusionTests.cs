using DocAssistant.Api.Modules.Search;

namespace DocAssistant.UnitTests.Search;

// Merging the keyword and vector rankings into one (docs/decisions.md #48).
public class ReciprocalRankFusionTests
{
    private const int Precision = 12;

    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();
    private static readonly Guid D = Guid.NewGuid();

    private readonly ReciprocalRankFusion _fusion = new();

    [Fact]
    public void ChunkFoundByBothSearchesBeatsTheFirstPlaceOfASingleSearch()
    {
        var fused = _fusion.Fuse([Ranking(A, B), Ranking(C, B)], limit: 10);

        Assert.Equal(B, fused[0].ChunkId);
    }

    [Fact]
    public void ScoreIsTheSumOfOneOverSixtyPlusThePositionInEachRanking()
    {
        var fused = _fusion.Fuse([Ranking(A, B, C), Ranking(C, D, A)], limit: 10);

        var scores = fused.ToDictionary(chunk => chunk.ChunkId, chunk => chunk.Score);

        Assert.Equal(1.0 / 61 + 1.0 / 63, scores[A], Precision);
        Assert.Equal(1.0 / 62, scores[B], Precision);
        Assert.Equal(1.0 / 63 + 1.0 / 61, scores[C], Precision);
        Assert.Equal(1.0 / 62, scores[D], Precision);
    }

    // The searches score on different scales, so only positions may count.
    [Fact]
    public void ScoresGivenByTheSearchesDoNotChangeTheResult()
    {
        IReadOnlyList<RankedChunk> keyword = [new(A, 0.9), new(B, 0.8), new(C, 0.1)];
        IReadOnlyList<RankedChunk> vector = [new(C, 0.7), new(D, 0.6)];

        IReadOnlyList<RankedChunk> keywordOtherScores = [new(A, 0.0001), new(B, -5), new(C, -1000)];
        IReadOnlyList<RankedChunk> vectorOtherScores = [new(C, 1_000_000), new(D, 3)];

        Assert.Equal(
            _fusion.Fuse([keyword, vector], limit: 10),
            _fusion.Fuse([keywordOtherScores, vectorOtherScores], limit: 10));
    }

    [Fact]
    public void ResultIsOrderedBestFirst()
    {
        var fused = _fusion.Fuse([Ranking(A, B, C), Ranking(B, D, A)], limit: 10);

        // B: 2nd + 1st, A: 1st + 3rd, D: 2nd, C: 3rd.
        Assert.Equal([B, A, D, C], Ids(fused));
        Assert.Equal(fused.Select(chunk => chunk.Score).OrderByDescending(score => score), fused.Select(chunk => chunk.Score));
    }

    [Fact]
    public void ChunkFoundByBothSearchesAppearsOnce()
    {
        var fused = _fusion.Fuse([Ranking(A, B), Ranking(B, A)], limit: 10);

        Assert.Equal(2, fused.Count);
        Assert.Equal(2, Ids(fused).Distinct().Count());
    }

    [Fact]
    public void SingleRankingKeepsItsOrder()
    {
        var fused = _fusion.Fuse([Ranking(C, A, D, B)], limit: 10);

        Assert.Equal([C, A, D, B], Ids(fused));
    }

    // Vector search finds nothing while chunks have no embeddings (decisions #51).
    [Fact]
    public void EmptyRankingLeavesTheOtherOneAsItIs()
    {
        Assert.Equal([A, B, C], Ids(_fusion.Fuse([Ranking(A, B, C), Ranking()], limit: 10)));
        Assert.Equal([A, B, C], Ids(_fusion.Fuse([Ranking(), Ranking(A, B, C)], limit: 10)));
    }

    [Fact]
    public void ChunkListedTwiceByOneSearchCountsOnceAtItsBestPosition()
    {
        var fused = _fusion.Fuse([Ranking(A, B, A, C)], limit: 10);

        Assert.Equal([A, B, C], Ids(fused));
        Assert.Equal(1.0 / 61, fused[0].Score, Precision);

        // The repeated entry takes no position: C is third, not fourth.
        Assert.Equal(1.0 / 63, fused[2].Score, Precision);
    }

    [Fact]
    public void ChunksWithTheSameScoreComeInTheOrderTheyWereMet()
    {
        var fused = _fusion.Fuse([Ranking(A, C), Ranking(B, D)], limit: 10);

        Assert.Equal([A, B, C, D], Ids(fused));
    }

    // 1/122 + 1/122 is exactly 1/61: found twice at position 62 scores the same as found
    // once at position 1. The better single position decides.
    [Fact]
    public void WithTheSameScoreTheBetterSinglePositionWins()
    {
        var before = Enumerable.Range(0, 61).Select(_ => Guid.NewGuid()).ToArray();
        var alsoBefore = Enumerable.Range(0, 61).Select(_ => Guid.NewGuid()).ToArray();

        var fused = _fusion.Fuse([Ranking([.. before, B]), Ranking([.. alsoBefore, B]), Ranking(A)], limit: 200);

        var a = fused.Single(chunk => chunk.ChunkId == A);
        var b = fused.Single(chunk => chunk.ChunkId == B);

        Assert.Equal(a.Score, b.Score);
        Assert.True(Ids(fused).IndexOf(A) < Ids(fused).IndexOf(B));
    }

    [Fact]
    public void SameRankingsAlwaysGiveTheSameResult()
    {
        IReadOnlyList<IReadOnlyList<RankedChunk>> rankings = [Ranking(A, B, C), Ranking(D, C, B)];

        var first = _fusion.Fuse(rankings, limit: 10);

        for (var run = 0; run < 20; run++)
        {
            Assert.Equal(first, _fusion.Fuse(rankings, limit: 10));
        }
    }

    [Fact]
    public void AtMostLimitChunksAreReturnedAndTheyAreTheBest()
    {
        var all = _fusion.Fuse([Ranking(A, B, C), Ranking(B, D, A)], limit: 10);

        var top = _fusion.Fuse([Ranking(A, B, C), Ranking(B, D, A)], limit: 2);

        Assert.Equal(all.Take(2), top);
    }

    [Fact]
    public void LimitAboveTheNumberOfChunksReturnsThemAll()
    {
        Assert.Equal(3, _fusion.Fuse([Ranking(A, B), Ranking(B, C)], limit: 100).Count);
    }

    [Fact]
    public void LimitOfZeroGivesNothing()
    {
        Assert.Empty(_fusion.Fuse([Ranking(A, B), Ranking(B, C)], limit: 0));
    }

    [Fact]
    public void NegativeLimitIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _fusion.Fuse([Ranking(A, B)], limit: -1));
    }

    [Fact]
    public void WithoutAnyFoundChunkTheResultIsEmpty()
    {
        Assert.Empty(_fusion.Fuse([], limit: 10));
        Assert.Empty(_fusion.Fuse([Ranking(), Ranking()], limit: 10));
    }

    // Scores fall with the position, as a real search would give them.
    private static IReadOnlyList<RankedChunk> Ranking(params Guid[] chunkIds) =>
        chunkIds.Select((id, index) => new RankedChunk(id, 1.0 / (index + 1))).ToList();

    private static List<Guid> Ids(IReadOnlyList<RankedChunk> chunks) =>
        chunks.Select(chunk => chunk.ChunkId).ToList();
}
