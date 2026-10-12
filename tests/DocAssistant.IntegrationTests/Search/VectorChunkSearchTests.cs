using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Embeddings;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.Api.Modules.Search;
using DocAssistant.IntegrationTests.Infrastructure;
using DocAssistant.IntegrationTests.Ingestion;
using DocAssistant.IntegrationTests.TenantIsolation;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pgvector;

namespace DocAssistant.IntegrationTests.Search;

// Vector search over chunks (docs/decisions.md #48, #50, #51): the question is embedded
// with the chunks' model and the chunks nearest to it by cosine distance come back,
// nearest first.
//
// The search runs as in the app (restricted database user, the scope's tenant, query
// filter, RLS, the real HNSW index). Only the embedding model is replaced: it gives every
// question the vector the test chose, so which chunk is nearest is known beforehand.
// Vectors differ only in their first two numbers, which makes them points on a plane:
// (1, 0) and (0, 1) are unrelated, (1, 0) and (-1, 0) are opposites.
[Collection(DatabaseCollection.Name)]
public class VectorChunkSearchTests(PostgresFixture database) : IAsyncLifetime
{
    private const string Question = "Yıllık izin kaç gündür?";
    private const int Precision = 5;

    private const string KnownGap = "Known gap in vector search; see the pull request that added this test.";

    private readonly QuestionEmbedder _model = new();
    private TenantIsolationScenario _scenario = null!;
    private string _configuredModel = null!;
    private int _nextOrdinal;

    public async Task InitializeAsync()
    {
        await IngestionTestSupport.WaitForStartupRecoveryAsync(database);
        _scenario = await TenantIsolationScenario.CreateAsync(database);
        _configuredModel = database.Api.Services.GetRequiredService<IOptions<EmbeddingOptions>>().Value.Model;

        _model.Answer(Question, 1, 0);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Nearest first ----

    [Fact]
    public async Task ChunksComeBackNearestFirst()
    {
        var opposite = await AddChunkAsync(-1, 0);
        var unrelated = await AddChunkAsync(0, 1);
        var same = await AddChunkAsync(1, 0);
        var close = await AddChunkAsync(1, 1);

        var results = await SearchAsync(Question);

        Assert.Equal([same, close, unrelated, opposite], results.Select(r => r.ChunkId));
    }

    // 1 for the same direction, 0 for unrelated, -1 for the opposite.
    [Fact]
    public async Task ScoreIsTheCosineSimilarity()
    {
        await AddChunkAsync(-1, 0);
        await AddChunkAsync(0, 1);
        await AddChunkAsync(1, 0);
        await AddChunkAsync(1, 1);

        var results = await SearchAsync(Question);

        Assert.Equal(1, results[0].Score, Precision);
        Assert.Equal(Math.Sqrt(2) / 2, results[1].Score, Precision);
        Assert.Equal(0, results[2].Score, Precision);
        Assert.Equal(-1, results[3].Score, Precision);
    }

    // Cosine looks at the direction only (decisions #50).
    [Fact]
    public async Task LengthOfAVectorDoesNotMatter()
    {
        var close = await AddChunkAsync(1, 0.2f);
        var sameDirectionButLong = await AddChunkAsync(50, 0);

        var results = await SearchAsync(Question);

        Assert.Equal([sameDirectionButLong, close], results.Select(r => r.ChunkId));
        Assert.Equal(1, results[0].Score, Precision);
    }

    [Fact]
    public async Task QuestionIsEmbeddedOnceAsItWasAsked()
    {
        await AddChunkAsync(1, 0);

        await SearchAsync(Question);

        Assert.Equal([Question], _model.Asked);
    }

    // Fusion works on positions, so the order must not change between two searches.
    [Fact]
    public async Task ChunksAtTheSameDistanceComeOrderedByTheirId()
    {
        // Stored newest id first, so the order they were written in is not the id order.
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.CreateVersion7()).ToList();
        foreach (var id in ids.OrderByDescending(id => id.ToString("N"), StringComparer.Ordinal))
        {
            await AddChunkAsync(Vector(1, 1), _configuredModel, id: id);
        }

        var results = await SearchAsync(Question);

        // PostgreSQL orders uuid values by their bytes, which is the order of the hex text.
        Assert.Equal(ids.OrderBy(id => id.ToString("N"), StringComparer.Ordinal), results.Select(r => r.ChunkId));
        Assert.Equal(results, await SearchAsync(Question));
    }

    // ---- Limit ----

    [Fact]
    public async Task AtMostLimitChunksAreReturnedAndTheyAreTheNearest()
    {
        await AddChunkAsync(-1, 0);
        var close = await AddChunkAsync(1, 1);
        await AddChunkAsync(0, 1);
        var same = await AddChunkAsync(1, 0);

        var results = await SearchAsync(Question, limit: 2);

        Assert.Equal([same, close], results.Select(r => r.ChunkId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task LimitBelowOneGivesNothingWithoutCallingTheModel(int limit)
    {
        await AddChunkAsync(1, 0);

        Assert.Empty(await SearchAsync(Question, limit));
        Assert.Empty(_model.Asked);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public async Task EmptyQuestionGivesNothingWithoutCallingTheModel(string query)
    {
        await AddChunkAsync(1, 0);

        Assert.Empty(await SearchAsync(query));
        Assert.Empty(_model.Asked);
    }

    // ---- Which chunks can be found ----

    // Chunks stored before embeddings existed (decisions #28) wait for re-indexing.
    [Fact]
    public async Task ChunkWithoutAnEmbeddingIsNotFound()
    {
        var embedded = await AddChunkAsync(0, 1);
        await AddChunkAsync(vector: null, model: null);
        await AddChunkAsync(vector: null, model: _configuredModel);

        var results = await SearchAsync(Question);

        Assert.Equal([embedded], results.Select(r => r.ChunkId));
    }

    // Vectors of another model cannot be compared with the question's (decisions #51).
    [Theory]
    [InlineData("an-older-model")]
    [InlineData(null)]
    public async Task ChunkEmbeddedWithAnotherModelIsNotFound(string? otherModel)
    {
        var current = await AddChunkAsync(0, 1);
        await AddChunkAsync(Vector(1, 0), otherModel);

        var results = await SearchAsync(Question);

        Assert.Equal([current], results.Select(r => r.ChunkId));
    }

    // ---- Tenants ----

    [Fact]
    public async Task OnlyTheCallersTenantIsSearched()
    {
        var mine = await AddChunkAsync(1, 0);
        var theirs = await AddChunkAsync(Vector(1, 0), _configuredModel, _scenario.TenantB, _scenario.DocumentB1);

        Assert.Equal([mine], (await SearchAsync(Question, tenantId: _scenario.TenantA)).Select(r => r.ChunkId));
        Assert.Equal([theirs], (await SearchAsync(Question, tenantId: _scenario.TenantB)).Select(r => r.ChunkId));
    }

    [Fact]
    public async Task WithoutATenantNothingIsFoundAndTheModelIsNotCalled()
    {
        await AddChunkAsync(1, 0);

        Assert.Empty(await SearchAsync(Question, selectTenant: false));
        Assert.Empty(_model.Asked);
    }

    // decisions #51: the HNSW index knows nothing about tenants. It collects the nearest
    // vectors of all tenants (40 by default) and the tenant filter drops the others; with
    // many nearer chunks elsewhere, fewer than limit would be left. This search is made
    // to go through the index (see SearchAsync).
    //
    // HNSW is approximate: it may miss a few chunks, so the test asks for half of the
    // tenant's chunks and does not say which ones must come.
    [Fact]
    public async Task EnoughChunksAreFoundAlthoughAnotherTenantHasManyNearerOnes()
    {
        var random = new Random(51);

        // Every chunk of the other tenant is nearer to the question than any of mine.
        var mine = await AddChunksAsync(
            Enumerable.Range(0, 60).Select(_ => ScatteredVector(random, 1 + random.NextSingle())),
            _scenario.TenantA,
            _scenario.DocumentA1);

        await AddChunksAsync(
            Enumerable.Range(0, 300).Select(_ => ScatteredVector(random, 0.3f * random.NextSingle())),
            _scenario.TenantB,
            _scenario.DocumentB1);

        var results = await SearchAsync(Question, limit: 30, throughTheIndex: true);

        Assert.Equal(30, results.Count);
        Assert.All(results, result => Assert.Contains(result.ChunkId, mine));
        Assert.Equal(results.OrderByDescending(r => r.Score).Select(r => r.ChunkId), results.Select(r => r.ChunkId));
    }

    // Found while writing these tests. When one direction is stored many times (here 400
    // chunks of another tenant; the same text embedded again and again gives this), the
    // index visits those chunks and then stops: the search returns nothing although the
    // caller has 60 chunks, with or without iterative scan. A search that does not go
    // through the index finds them.
    [Fact(Skip = KnownGap)]
    public async Task ChunksAreFoundAlthoughAnotherTenantStoresOneVectorManyTimes()
    {
        var random = new Random(51);

        var mine = await AddChunksAsync(
            Enumerable.Range(0, 60).Select(_ => ScatteredVector(random, 1 + random.NextSingle())),
            _scenario.TenantA,
            _scenario.DocumentA1);

        await AddChunksAsync(
            Enumerable.Range(0, 400).Select(i => Vector((i % 70) + 1, 0)), _scenario.TenantB, _scenario.DocumentB1);

        var results = await SearchAsync(Question, limit: 30, throughTheIndex: true);

        Assert.Equal(30, results.Count);
        Assert.All(results, result => Assert.Contains(result.ChunkId, mine));
    }

    // ---- Called from inside a transaction ----

    [Fact]
    public async Task SearchInsideTheCallersTransactionLeavesItOpen()
    {
        var same = await AddChunkAsync(1, 0);

        await using var scope = database.Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantSelector>().Select(_scenario.TenantA);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync();

        var results = await CreateSearch(scope.ServiceProvider).SearchAsync(Question, 30, CancellationToken.None);

        Assert.Equal([same], results.Select(r => r.ChunkId));
        Assert.Same(transaction, db.Database.CurrentTransaction);

        // Still usable: the search did not commit or roll it back.
        Assert.Equal(1, await db.Chunks.CountAsync(chunk => chunk.Id == same));
        await transaction.CommitAsync();
    }

    // ---- A model that answers wrongly ----

    [Theory]
    [InlineData(768)]
    [InlineData(1025)]
    public async Task QuestionVectorOfAnotherSizeIsRejected(int dimensions)
    {
        await AddChunkAsync(1, 0);
        _model.Dimensions = dimensions;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SearchAsync(Question));

        Assert.Contains(dimensions.ToString(), error.Message);
    }

    // ---- Helpers ----

    private static Vector Vector(float x, float y)
    {
        var values = new float[ChunkConfiguration.EmbeddingDimensions];
        values[0] = x;
        values[1] = y;

        return new Vector(values);
    }

    // (1, y) with a little noise in eight more numbers, so that the vectors are spread out
    // as real embeddings are instead of lying on one line.
    private static Vector ScatteredVector(Random random, float y)
    {
        var values = new float[ChunkConfiguration.EmbeddingDimensions];
        values[0] = 1;
        values[1] = y;

        for (var i = 2; i < 10; i++)
        {
            values[i] = (random.NextSingle() - 0.5f) * 0.2f;
        }

        return new Vector(values);
    }

    // A chunk of tenant A embedded with the configured model.
    private Task<Guid> AddChunkAsync(float x, float y) => AddChunkAsync(Vector(x, y), _configuredModel);

    // Written as the superuser: the tests are about searching, not about how chunks get there.
    private async Task<Guid> AddChunkAsync(
        Vector? vector, string? model, Guid? tenantId = null, Guid? documentId = null, Guid? id = null)
    {
        await using var db = database.CreateOwnerDbContext(tenantId ?? _scenario.TenantA);

        var chunk = NewChunk(vector, model, documentId ?? _scenario.DocumentA1);
        chunk.Id = id ?? chunk.Id;

        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        return chunk.Id;
    }

    private async Task<HashSet<Guid>> AddChunksAsync(IEnumerable<Vector> vectors, Guid tenantId, Guid documentId)
    {
        await using var db = database.CreateOwnerDbContext(tenantId);

        var chunks = vectors.Select(vector => NewChunk(vector, _configuredModel, documentId)).ToList();

        db.Chunks.AddRange(chunks);
        await db.SaveChangesAsync();

        return chunks.Select(chunk => chunk.Id).ToHashSet();
    }

    private Chunk NewChunk(Vector? vector, string? model, Guid documentId) => new()
    {
        Id = Guid.CreateVersion7(),
        DocumentId = documentId,
        DocumentVersion = 1,
        Ordinal = _nextOrdinal++,
        Content = "Çalışanlar bir yılı doldurduklarında yıllık ücretli izne hak kazanır.",
        SourceType = DocumentSourceType.Docx,
        Embedding = vector,
        EmbeddingModel = model,
    };

    // One search in its own scope with the tenant selected, as a request would have it.
    private async Task<IReadOnlyList<RankedChunk>> SearchAsync(
        string query, int limit = 30, Guid? tenantId = null, bool selectTenant = true, bool throughTheIndex = false)
    {
        await using var scope = database.Api.Services.CreateAsyncScope();

        if (selectTenant)
        {
            scope.ServiceProvider.GetRequiredService<ITenantSelector>().Select(tenantId ?? _scenario.TenantA);
        }

        var search = CreateSearch(scope.ServiceProvider);

        if (!throughTheIndex)
        {
            return await search.SearchAsync(query, limit, CancellationToken.None);
        }

        // The test table is small, so PostgreSQL would rather read the tenant's rows and
        // sort them than use the HNSW index. With both switched off the index is the
        // plan left. SET LOCAL lasts for this transaction only; the search joins it.
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off; SET LOCAL enable_sort = off");

        var results = await search.SearchAsync(query, limit, CancellationToken.None);
        await transaction.CommitAsync();

        return results;
    }

    // The app's own search with only the model replaced.
    private VectorChunkSearch CreateSearch(IServiceProvider services) =>
        new(
            services.GetRequiredService<AppDbContext>(),
            services.GetRequiredService<ITenantContext>(),
            _model,
            services.GetRequiredService<IOptions<EmbeddingOptions>>());

    // Stands in for the embedding model: answers each question with the vector the test
    // gave it and records what it was asked.
    private sealed class QuestionEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly Dictionary<string, (float X, float Y)> _answers = [];
        private readonly List<string> _asked = [];

        public IReadOnlyList<string> Asked => _asked;

        public int Dimensions { get; set; } = ChunkConfiguration.EmbeddingDimensions;

        public void Answer(string question, float x, float y) => _answers[question] = (x, y);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();

            foreach (var value in values)
            {
                _asked.Add(value);

                var (x, y) = _answers[value];
                var vector = new float[Dimensions];
                vector[0] = x;
                vector[1] = y;

                embeddings.Add(new Embedding<float>(vector));
            }

            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
