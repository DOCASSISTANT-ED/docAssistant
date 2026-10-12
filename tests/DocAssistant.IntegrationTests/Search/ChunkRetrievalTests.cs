using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Embeddings;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.Api.Modules.Search;
using DocAssistant.IntegrationTests.Infrastructure;
using DocAssistant.IntegrationTests.Ingestion;
using DocAssistant.IntegrationTests.TenantIsolation;
using DocAssistant.Shared.Tenancy;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pgvector;

namespace DocAssistant.IntegrationTests.Search;

// The search pipeline's entry point (docs/decisions.md #48): keyword and vector search,
// fusion of the two rankings, the candidates' text loaded once, then the reranker when
// there is one.
//
// Most tests replace the two searches by fixed rankings, so that the fused order is known
// and what the entry point itself does (fusing, loading, reranking, limits) can be told
// apart from what the searches do; fusion and the database are real. The last group runs
// the pipeline as the app registers it, with only the embedding model replaced.
[Collection(DatabaseCollection.Name)]
public class ChunkRetrievalTests(PostgresFixture database) : IAsyncLifetime
{
    private const string Question = "Günlük harcırah ne kadardır?";
    private const int Precision = 12;

    private TenantIsolationScenario _scenario = null!;
    private int _nextOrdinal;

    public async Task InitializeAsync()
    {
        await IngestionTestSupport.WaitForStartupRecoveryAsync(database);
        _scenario = await TenantIsolationScenario.CreateAsync(database);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Fusing the two searches ----

    [Fact]
    public async Task RankingsOfTheTwoSearchesAreFused()
    {
        var a = await AddChunkAsync("a");
        var b = await AddChunkAsync("b");
        var c = await AddChunkAsync("c");
        var d = await AddChunkAsync("d");

        var results = await RetrieveAsync(keyword: [a, b, c], vector: [c, d]);

        // c was found by both; a is a first place; b and d are second places, and the
        // keyword search is listed first.
        Assert.Equal([c, a, b, d], results.Select(r => r.ChunkId));
    }

    [Fact]
    public async Task BothSearchesGetTheQuestionAndTheConfiguredNumberOfCandidates()
    {
        var keyword = new FixedRanking();
        var vector = new FixedRanking();

        await RetrieveAsync(keyword, vector, new SearchOptions { CandidatesPerSearch = 17 });

        Assert.Equal([(Question, 17)], keyword.Calls);
        Assert.Equal([(Question, 17)], vector.Calls);
    }

    [Fact]
    public async Task ResultOfASingleSearchIsEnough()
    {
        var a = await AddChunkAsync("a");
        var b = await AddChunkAsync("b");

        Assert.Equal([a, b], (await RetrieveAsync(keyword: [a, b], vector: [])).Select(r => r.ChunkId));
        Assert.Equal([b, a], (await RetrieveAsync(keyword: [], vector: [b, a])).Select(r => r.ChunkId));
    }

    // ---- What a result carries ----

    // Chat quotes the source from these; evaluation matches evidence in the text (#47).
    [Fact]
    public async Task ResultCarriesTheChunkWithItsDocumentAndPlace()
    {
        var chunk = await AddChunkAsync(
            "Belge: A-2 > Bölüm 3: Harcırah\nGünlük harcırah 1.500 TL'dir.",
            documentId: _scenario.DocumentA2,
            sectionPath: "Bölüm 3: Harcırah",
            pageNumber: 4);

        var result = Assert.Single(await RetrieveAsync(keyword: [chunk], vector: [chunk]));

        Assert.Equal(chunk, result.ChunkId);
        Assert.Equal(_scenario.DocumentA2, result.DocumentId);
        Assert.Equal("A-2", result.DocumentTitle);
        Assert.Equal("Bölüm 3: Harcırah", result.SectionPath);
        Assert.Equal(4, result.PageNumber);
        Assert.Equal("Belge: A-2 > Bölüm 3: Harcırah\nGünlük harcırah 1.500 TL'dir.", result.Content);
    }

    // A DOCX chunk before the first heading has neither (decisions #22, #46).
    [Fact]
    public async Task SectionAndPageMayBeMissing()
    {
        var chunk = await AddChunkAsync("Giriş metni.", sectionPath: null, pageNumber: null);

        var result = Assert.Single(await RetrieveAsync(keyword: [chunk], vector: []));

        Assert.Null(result.SectionPath);
        Assert.Null(result.PageNumber);
    }

    [Fact]
    public async Task WithoutARerankerTheScoreIsTheFusionScore()
    {
        var both = await AddChunkAsync("a");
        var one = await AddChunkAsync("b");

        var results = await RetrieveAsync(keyword: [both, one], vector: [both]);

        Assert.Equal(2.0 / 61, results[0].Score, Precision);
        Assert.Equal(1.0 / 62, results[1].Score, Precision);
    }

    // ---- Without a reranker ----

    [Fact]
    public async Task WithoutARerankerTheBestOfTheFusedOrderAreReturned()
    {
        var chunks = await AddChunksAsync(12);

        var results = await RetrieveAsync(keyword: chunks, vector: [], new SearchOptions { Results = 5 });

        Assert.Equal(chunks.Take(5), results.Select(r => r.ChunkId));
    }

    // ---- With a reranker ----

    [Fact]
    public async Task RerankerDecidesTheFinalOrder()
    {
        var a = await AddChunkAsync("a");
        var b = await AddChunkAsync("b");
        var c = await AddChunkAsync("c");
        var reranker = new FakeReranker(candidates => [new(c, 0.9), new(a, 0.4), new(b, 0.1)]);

        var results = await RetrieveAsync(keyword: [a, b, c], vector: [], reranker: reranker);

        Assert.Equal([c, a, b], results.Select(r => r.ChunkId));
        Assert.Equal([0.9, 0.4, 0.1], results.Select(r => r.Score));
    }

    [Fact]
    public async Task RerankerGetsTheQuestionTheCandidatesTextAndTheNumberOfResults()
    {
        var a = await AddChunkAsync("Günlük harcırah 1.500 TL'dir.");
        var b = await AddChunkAsync("Yıllık izin 14 iş günüdür.");
        var reranker = new FakeReranker();

        await RetrieveAsync(keyword: [a, b], vector: [b], new SearchOptions { Results = 6 }, reranker);

        var call = Assert.Single(reranker.Calls);
        Assert.Equal(Question, call.Query);
        Assert.Equal(6, call.Limit);

        // In the fused order: b was found by both searches.
        Assert.Equal(
            [new ChunkText(b, "Yıllık izin 14 iş günüdür."), new ChunkText(a, "Günlük harcırah 1.500 TL'dir.")],
            call.Candidates);
    }

    // More candidates than results go to the reranker: it may lift a chunk from far down.
    [Fact]
    public async Task RerankerSeesTheConfiguredNumberOfCandidatesNotOnlyTheResults()
    {
        var chunks = await AddChunksAsync(12);
        var reranker = new FakeReranker(candidates => [new(candidates[^1].ChunkId, 1)]);
        var options = new SearchOptions { CandidatesForReranking = 10, Results = 3 };

        var results = await RetrieveAsync(keyword: chunks, vector: [], options, reranker);

        Assert.Equal(chunks.Take(10), Assert.Single(reranker.Calls).Candidates.Select(c => c.ChunkId));
        Assert.Equal([chunks[9]], results.Select(r => r.ChunkId));
    }

    [Fact]
    public async Task NoMoreThanTheConfiguredResultsComeBackEvenIfTheRerankerReturnsMore()
    {
        var chunks = await AddChunksAsync(8);
        var reranker = new FakeReranker(candidates => candidates.Select((c, i) => new RankedChunk(c.ChunkId, 1.0 - (i * 0.1))).ToList());

        var results = await RetrieveAsync(keyword: chunks, vector: [], new SearchOptions { Results = 3 }, reranker);

        Assert.Equal(chunks.Take(3), results.Select(r => r.ChunkId));
    }

    // ---- Nothing to search or nothing found ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public async Task EmptyQuestionGivesNothingAndNoSearchRuns(string query)
    {
        var keyword = new FixedRanking(await AddChunkAsync("a"));
        var vector = new FixedRanking();
        var reranker = new FakeReranker();

        Assert.Empty(await RetrieveAsync(keyword, vector, reranker: reranker, query: query));

        Assert.Empty(keyword.Calls);
        Assert.Empty(vector.Calls);
        Assert.Empty(reranker.Calls);
    }

    [Fact]
    public async Task WhenNeitherSearchFindsAnythingTheRerankerIsNotCalled()
    {
        var reranker = new FakeReranker();

        Assert.Empty(await RetrieveAsync(keyword: [], vector: [], reranker: reranker));
        Assert.Empty(reranker.Calls);
    }

    // ---- Chunks that cannot be loaded ----

    // Its document was processed again or deleted between the search and the load.
    [Fact]
    public async Task ChunkThatDisappearedAfterTheSearchIsLeftOut()
    {
        var a = await AddChunkAsync("a");
        var gone = Guid.CreateVersion7();
        var b = await AddChunkAsync("b");
        var reranker = new FakeReranker();

        var withoutReranker = await RetrieveAsync(keyword: [a, gone, b], vector: []);
        var withReranker = await RetrieveAsync(keyword: [a, gone, b], vector: [], reranker: reranker);

        Assert.Equal([a, b], withoutReranker.Select(r => r.ChunkId));
        Assert.Equal([a, b], withReranker.Select(r => r.ChunkId));
        Assert.Equal([a, b], Assert.Single(reranker.Calls).Candidates.Select(c => c.ChunkId));
    }

    // Even if a search were to hand over another tenant's chunk, its text is loaded
    // through the query filter and RLS and never reaches the caller or the reranker.
    [Fact]
    public async Task ChunkOfAnotherTenantIsNeverLoaded()
    {
        var mine = await AddChunkAsync("benim");
        var theirs = await AddChunkAsync("onların", tenantId: _scenario.TenantB, documentId: _scenario.DocumentB1);
        var reranker = new FakeReranker();

        var results = await RetrieveAsync(keyword: [theirs, mine], vector: [theirs], reranker: reranker);

        Assert.Equal([mine], results.Select(r => r.ChunkId));
        Assert.Equal([new ChunkText(mine, "benim")], Assert.Single(reranker.Calls).Candidates);
    }

    // ---- The pipeline as the app registers it ----

    // Real keyword search, vector search, fusion and entry point from the app's own
    // registrations; only the embedding model is replaced. The reranker is off, as it is
    // without a "Reranker" section (decisions #52).
    [Fact]
    public async Task RegisteredPipelinePutsTheChunkBothSearchesFindFirst()
    {
        // Keyword search finds the two "harcırah" chunks; the model puts the question at
        // (1, 0), so vector search ranks "both" first, then "meaning", then "words".
        var words = await AddChunkAsync("Harcırah harcırah: günlük harcırah talepleri.", vector: Direction(0, 1));
        var both = await AddChunkAsync("Günlük harcırah 1.500 TL'dir.", vector: Direction(1, 0));
        var meaning = await AddChunkAsync("Şehir dışı görevlerde yolluk ödenir.", vector: Direction(1, 0.5f));

        var results = await RetrieveWithTheRegisteredPipelineAsync(_scenario.TenantA);

        Assert.Equal(both, results[0].ChunkId);
        Assert.Equal(new[] { both, meaning, words }.Order(), results.Select(r => r.ChunkId).Order());
        Assert.Equal("A-1", results[0].DocumentTitle);
    }

    [Fact]
    public async Task RegisteredPipelineReturnsOnlyTheCallersChunks()
    {
        var mine = await AddChunkAsync("Günlük harcırah 1.500 TL'dir.", vector: Direction(1, 0));
        var theirs = await AddChunkAsync(
            "Günlük harcırah 1.500 TL'dir.", _scenario.TenantB, _scenario.DocumentB1, vector: Direction(1, 0));

        Assert.Equal([mine], (await RetrieveWithTheRegisteredPipelineAsync(_scenario.TenantA)).Select(r => r.ChunkId));
        Assert.Equal([theirs], (await RetrieveWithTheRegisteredPipelineAsync(_scenario.TenantB)).Select(r => r.ChunkId));
    }

    [Fact]
    public async Task RegisteredPipelineReturnsAtMostTheDefaultNumberOfResults()
    {
        for (var i = 0; i < 12; i++)
        {
            await AddChunkAsync($"Harcırah kuralı {i}.", vector: Direction(1, i * 0.1f));
        }

        var results = await RetrieveWithTheRegisteredPipelineAsync(_scenario.TenantA);

        Assert.Equal(new SearchOptions().Results, results.Count);
    }

    // ---- Helpers ----

    private static Vector Direction(float x, float y)
    {
        var values = new float[ChunkConfiguration.EmbeddingDimensions];
        values[0] = x;
        values[1] = y;

        return new Vector(values);
    }

    // One chunk in tenant A's first document unless told otherwise, written as the
    // superuser: the tests are about retrieval, not about how chunks get there.
    private async Task<Guid> AddChunkAsync(
        string content,
        Guid? tenantId = null,
        Guid? documentId = null,
        string? sectionPath = "Bölüm 1",
        int? pageNumber = 1,
        Vector? vector = null)
    {
        await using var db = database.CreateOwnerDbContext(tenantId ?? _scenario.TenantA);

        var chunk = new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = documentId ?? _scenario.DocumentA1,
            DocumentVersion = 1,
            Ordinal = _nextOrdinal++,
            Content = content,
            SectionPath = sectionPath,
            PageNumber = pageNumber,
            SourceType = DocumentSourceType.Pdf,
            Embedding = vector,
            EmbeddingModel = vector is null ? null : ConfiguredModel,
        };

        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        return chunk.Id;
    }

    private async Task<Guid[]> AddChunksAsync(int count)
    {
        var ids = new Guid[count];

        for (var i = 0; i < count; i++)
        {
            ids[i] = await AddChunkAsync($"chunk {i}");
        }

        return ids;
    }

    private string ConfiguredModel =>
        database.Api.Services.GetRequiredService<IOptions<EmbeddingOptions>>().Value.Model;

    private Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        Guid[] keyword, Guid[] vector, SearchOptions? options = null, IReranker? reranker = null) =>
        RetrieveAsync(new FixedRanking(keyword), new FixedRanking(vector), options, reranker);

    // The real entry point and fusion in a request-like scope (tenant A selected, the
    // restricted database user), with the given rankings standing in for the searches.
    private async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        FixedRanking keyword,
        FixedRanking vector,
        SearchOptions? options = null,
        IReranker? reranker = null,
        string query = Question)
    {
        await using var scope = database.Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantSelector>().Select(_scenario.TenantA);

        var retrieval = new ChunkRetrieval(
            keyword,
            vector,
            scope.ServiceProvider.GetRequiredService<IRankFusion>(),
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            Options.Create(options ?? new SearchOptions()),
            reranker);

        return await retrieval.RetrieveAsync(query, CancellationToken.None);
    }

    private async Task<IReadOnlyList<SearchResult>> RetrieveWithTheRegisteredPipelineAsync(Guid tenantId)
    {
        await using var api = database.Api.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FixedQuestionEmbedder())));

        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantSelector>().Select(tenantId);

        return await scope.ServiceProvider.GetRequiredService<IChunkRetrieval>()
            .RetrieveAsync(Question, CancellationToken.None);
    }

    // Stands in for either search: always the same ranking, cut to the asked limit.
    private sealed class FixedRanking(params Guid[] chunkIds) : IKeywordChunkSearch, IVectorChunkSearch
    {
        private readonly List<(string Query, int Limit)> _calls = [];

        public IReadOnlyList<(string Query, int Limit)> Calls => _calls;

        public Task<IReadOnlyList<RankedChunk>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        {
            _calls.Add((query, limit));

            return Task.FromResult<IReadOnlyList<RankedChunk>>(
                chunkIds.Take(limit).Select((id, index) => new RankedChunk(id, 1.0 / (index + 1))).ToList());
        }
    }

    // Records what it was given. Without an order of its own it keeps the candidates'
    // order, with falling scores.
    private sealed class FakeReranker(Func<IReadOnlyList<ChunkText>, IReadOnlyList<RankedChunk>>? rerank = null) : IReranker
    {
        private readonly List<(string Query, IReadOnlyList<ChunkText> Candidates, int Limit)> _calls = [];

        public IReadOnlyList<(string Query, IReadOnlyList<ChunkText> Candidates, int Limit)> Calls => _calls;

        public Task<IReadOnlyList<RankedChunk>> RerankAsync(
            string query, IReadOnlyList<ChunkText> candidates, int limit, CancellationToken cancellationToken)
        {
            _calls.Add((query, candidates, limit));

            return Task.FromResult(
                rerank?.Invoke(candidates)
                ?? candidates.Take(limit).Select((c, index) => new RankedChunk(c.ChunkId, 1.0 / (index + 1))).ToList());
        }
    }

    // The embedding model for the registered pipeline: every question is at (1, 0).
    private sealed class FixedQuestionEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(Direction(1, 0).ToArray()))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
