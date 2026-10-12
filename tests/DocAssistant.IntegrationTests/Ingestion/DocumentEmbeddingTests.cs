using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Embeddings;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.IntegrationTests.Documents;
using DocAssistant.IntegrationTests.Infrastructure;
using DocAssistant.IntegrationTests.TenantIsolation;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocAssistant.IntegrationTests.Ingestion;

// Embedding as part of processing (docs/decisions.md #50): parse, chunk, embed, save.
// Every chunk is stored with its vector and the model's name, and a model that cannot be
// used leaves the document to be tried again instead of failing it.
//
// Each test handles one queue message the way the worker does, with the real handler,
// processor, parsers and chunker. The embedding model is replaced by one that records
// what it was asked and can be told to misbehave; no test calls Ollama.
[Collection(DatabaseCollection.Name)]
public class DocumentEmbeddingTests(PostgresFixture database) : IAsyncLifetime
{
    private const string Title = "İK Yönetmeliği";
    private const string SampleDocx = "ik-yonetmeligi.docx";
    private const int SampleChunkCount = 5;

    private readonly InMemoryFileStorage _storage = new();
    private readonly RecordingEmbeddingGenerator _model = new();
    private TenantIsolationScenario _scenario = null!;

    // Set while the model is being called: was a database transaction open at that moment?
    private bool? _transactionOpenWhileEmbedding;

    public async Task InitializeAsync()
    {
        await IngestionTestSupport.WaitForStartupRecoveryAsync(database);
        _scenario = await TenantIsolationScenario.CreateAsync(database);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Chunks are stored with their vectors ----

    [Fact]
    public async Task EveryChunkIsStoredWithAVectorOfTheModelsSize()
    {
        var documentId = await AddSampleDocumentAsync();

        await HandleAsync(documentId);

        var chunks = await ReadChunksAsync(documentId);
        Assert.Equal(SampleChunkCount, chunks.Count);
        Assert.All(chunks, chunk =>
        {
            Assert.NotNull(chunk.Embedding);
            Assert.Equal(ChunkConfiguration.EmbeddingDimensions, chunk.Embedding.ToArray().Length);
        });
    }

    // Re-indexing later finds chunks of an older model by this name.
    [Fact]
    public async Task EveryChunkCarriesTheConfiguredModelName()
    {
        var documentId = await AddSampleDocumentAsync();

        await HandleAsync(documentId);

        var configured = database.Api.Services.GetRequiredService<IOptions<EmbeddingOptions>>().Value.Model;
        Assert.All(await ReadChunksAsync(documentId), chunk => Assert.Equal(configured, chunk.EmbeddingModel));
    }

    // decisions #27, #50: the model sees what search will later return, header included.
    [Fact]
    public async Task EmbeddedTextIsTheWholeChunkContentWithItsContextHeader()
    {
        var documentId = await AddSampleDocumentAsync();

        await HandleAsync(documentId);

        var chunks = await ReadChunksAsync(documentId);
        Assert.Equal(chunks.Select(chunk => chunk.Content), _model.Texts);
        Assert.All(_model.Texts, text => Assert.StartsWith($"Belge: {Title}", text));
    }

    [Fact]
    public async Task EachChunkStoresTheVectorOfItsOwnText()
    {
        var documentId = await AddSampleDocumentAsync();

        await HandleAsync(documentId);

        var chunks = await ReadChunksAsync(documentId);
        Assert.Equal(SampleChunkCount, chunks.Select(chunk => chunk.Embedding!.ToArray()[0]).Distinct().Count());
        Assert.All(chunks, chunk => Assert.Equal(_model.VectorOf(chunk.Content), chunk.Embedding!.ToArray()));
    }

    [Fact]
    public async Task DocumentWithEmbeddedChunksIsDone()
    {
        var documentId = await AddSampleDocumentAsync();

        await HandleAsync(documentId);

        var document = await IngestionTestSupport.ReadAsync(database, documentId);
        Assert.Equal(DocumentStatus.Done, document.Status);
        Assert.NotNull(document.ProcessedAt);
    }

    // ---- Large documents ----

    // Several requests instead of one that could run into the HTTP timeout.
    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(32, new[] { 32 })]
    [InlineData(33, new[] { 32, 1 })]
    [InlineData(70, new[] { 32, 32, 6 })]
    public async Task ChunksAreSentToTheModelInBatches(int chunkCount, int[] expectedBatchSizes)
    {
        var documentId = await AddDocumentAsync();

        await HandleAsync(documentId, SectionsParser(chunkCount));

        Assert.Equal(expectedBatchSizes, _model.Requests.Select(request => request.Count));
    }

    [Fact]
    public async Task ChunksOfALargeDocumentKeepTheirOwnVectorsAcrossBatches()
    {
        var documentId = await AddDocumentAsync();

        await HandleAsync(documentId, SectionsParser(70));

        var chunks = await ReadChunksAsync(documentId);
        Assert.Equal(70, chunks.Count);
        Assert.Equal(chunks.Select(chunk => chunk.Content), _model.Texts);
        Assert.All(chunks, chunk => Assert.Equal(_model.VectorOf(chunk.Content), chunk.Embedding!.ToArray()));
    }

    // ---- The model is not held inside the transaction ----

    // The model can be slow; no connection or lock should wait for it.
    [Fact]
    public async Task ModelIsCalledBeforeTheDatabaseTransactionOpens()
    {
        var documentId = await AddSampleDocumentAsync();

        await HandleAsync(documentId);

        Assert.False(_transactionOpenWhileEmbedding);
    }

    // A document that cannot be read never reaches the model.
    [Fact]
    public async Task ModelIsNotCalledForADocumentWithoutChunks()
    {
        var documentId = await AddDocumentAsync();
        var parser = new StubParser(new HeadingBlock("Bölüm 1", 1, 1), new HeadingBlock("Bölüm 2", 1, 1));

        await HandleAsync(documentId, parser);

        Assert.Empty(_model.Requests);
        Assert.Equal(DocumentStatus.Failed, (await IngestionTestSupport.ReadAsync(database, documentId)).Status);
    }

    // ---- A model that cannot be used ----

    // Not the document's fault: it is not Failed, and recovery tries again (decisions #38).
    [Fact]
    public async Task UnreachableModelLeavesTheDocumentProcessing()
    {
        var documentId = await AddSampleDocumentAsync();
        _model.Failure = new HttpRequestException("Connection refused.");

        await Assert.ThrowsAsync<HttpRequestException>(() => HandleAsync(documentId));

        await AssertStillProcessingWithoutChunksAsync(documentId);
    }

    // The column holds 1024 numbers; another model is a configuration mistake.
    [Theory]
    [InlineData(768)]
    [InlineData(1023)]
    [InlineData(1025)]
    public async Task VectorOfAnotherSizeStopsProcessing(int dimensions)
    {
        var documentId = await AddSampleDocumentAsync();
        _model.Dimensions = dimensions;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HandleAsync(documentId));

        Assert.Contains(dimensions.ToString(), error.Message);
        await AssertStillProcessingWithoutChunksAsync(documentId);
    }

    [Fact]
    public async Task FewerVectorsThanChunksStopsProcessing()
    {
        var documentId = await AddSampleDocumentAsync();
        _model.VectorsLeftOut = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => HandleAsync(documentId));

        await AssertStillProcessingWithoutChunksAsync(documentId);
    }

    // A wrong vector in a later batch must not leave the earlier batches behind.
    [Fact]
    public async Task FailureInALaterBatchStoresNothing()
    {
        var documentId = await AddDocumentAsync();
        _model.FailFromRequest = 2;

        await Assert.ThrowsAsync<HttpRequestException>(() => HandleAsync(documentId, SectionsParser(70)));

        Assert.Equal(2, _model.Requests.Count);
        await AssertStillProcessingWithoutChunksAsync(documentId);
    }

    // Chunks of an earlier run stay searchable until the new set is complete (decisions #35).
    [Fact]
    public async Task FailingModelLeavesTheChunksOfAnEarlierRunAlone()
    {
        var documentId = await AddSampleDocumentAsync();
        await AddStaleChunksAsync(documentId, count: 3);
        _model.Failure = new HttpRequestException("Connection refused.");

        await Assert.ThrowsAsync<HttpRequestException>(() => HandleAsync(documentId));

        var chunks = await ReadChunksAsync(documentId);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.StartsWith(StaleContent, chunk.Content));
    }

    [Fact]
    public async Task DocumentIsEmbeddedOnTheNextAttemptOnceTheModelIsBack()
    {
        var documentId = await AddSampleDocumentAsync();
        _model.Failure = new HttpRequestException("Connection refused.");
        await Assert.ThrowsAsync<HttpRequestException>(() => HandleAsync(documentId));

        _model.Failure = null;
        await HandleAsync(documentId);

        var document = await IngestionTestSupport.ReadAsync(database, documentId);
        Assert.Equal(DocumentStatus.Done, document.Status);
        Assert.Equal(2, document.AttemptCount);

        var chunks = await ReadChunksAsync(documentId);
        Assert.Equal(SampleChunkCount, chunks.Count);
        Assert.All(chunks, chunk => Assert.NotNull(chunk.Embedding));
    }

    // ---- Helpers ----

    private const string StaleContent = "stale chunk";

    private Task<Guid> AddSampleDocumentAsync() =>
        AddDocumentAsync(
            DocumentContentTypes.Docx,
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", SampleDocx)));

    // A Pending document of tenant A, as the upload endpoint leaves it. The default file
    // is never read: tests that use it pass their own parser.
    private async Task<Guid> AddDocumentAsync(string contentType = DocumentContentTypes.Pdf, byte[]? file = null)
    {
        var document = TestDocuments.New(Title, _scenario.Alice);
        document.Id = Guid.CreateVersion7();
        document.ContentType = contentType;

        using (var content = new MemoryStream(file ?? [1, 2, 3]))
        {
            await _storage.SaveAsync(document.StorageKey, content, contentType);
        }

        await using var db = database.CreateOwnerDbContext(_scenario.TenantA);
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        return document.Id;
    }

    private async Task AddStaleChunksAsync(Guid documentId, int count)
    {
        await using var db = database.CreateOwnerDbContext(_scenario.TenantA);

        db.Chunks.AddRange(Enumerable.Range(0, count).Select(ordinal => new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = documentId,
            DocumentVersion = 1,
            Ordinal = ordinal,
            Content = $"{StaleContent} {ordinal}",
            SourceType = DocumentSourceType.Pdf,
        }));

        await db.SaveChangesAsync();
    }

    // A document of chunkCount short sections; each section becomes one chunk. The single
    // level-1 heading is the document's name (decisions #46).
    private static StubParser SectionsParser(int chunkCount)
    {
        var blocks = new List<DocumentBlock> { new HeadingBlock(Title, 1, 1) };

        for (var section = 1; section <= chunkCount; section++)
        {
            blocks.Add(new HeadingBlock($"Madde {section}", 2, 1));
            blocks.Add(new ParagraphBlock($"Bu, {section}. maddenin metnidir.", 1));
        }

        return new StubParser([.. blocks]);
    }

    // The stored truth, read as the superuser with filters off.
    private async Task<List<Chunk>> ReadChunksAsync(Guid documentId)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Chunks.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.DocumentId == documentId)
            .OrderBy(c => c.Ordinal)
            .ToListAsync();
    }

    private async Task AssertStillProcessingWithoutChunksAsync(Guid documentId)
    {
        var document = await IngestionTestSupport.ReadAsync(database, documentId);

        Assert.Equal(DocumentStatus.Processing, document.Status);
        Assert.Null(document.FailureReason);
        Assert.Null(document.ProcessedAt);
        Assert.Equal(1, document.AttemptCount);
        Assert.Empty(await ReadChunksAsync(documentId));
    }

    // One message, handled in its own scope, exactly as IngestionWorker does it. Passing a
    // parser replaces the real ones.
    private async Task HandleAsync(Guid documentId, StubParser? parser = null)
    {
        await using var scope = database.Api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var db = services.GetRequiredService<AppDbContext>();
        _model.OnRequest = () => _transactionOpenWhileEmbedding = db.Database.CurrentTransaction is not null;

        var processor = new DocumentProcessor(
            db,
            _storage,
            parser is null ? services.GetServices<IDocumentParser>() : [parser],
            _model,
            services.GetRequiredService<IOptions<EmbeddingOptions>>(),
            TimeProvider.System);

        var handler = new DocumentIngestionHandler(
            db,
            services.GetRequiredService<ITenantSelector>(),
            processor,
            NullLogger<DocumentIngestionHandler>.Instance);

        await handler.HandleAsync(new IngestionWorkItem(_scenario.TenantA, documentId), CancellationToken.None);
    }

    // Returns fixed blocks whatever the file holds.
    private sealed class StubParser(params DocumentBlock[] blocks) : IDocumentParser
    {
        public DocumentSourceType SourceType => DocumentSourceType.Pdf;

        public ParsedDocument Parse(Stream content) => new(blocks);
    }

    // Stands in for the embedding model. Every distinct text gets its own vector (the
    // first number tells them apart), so a test can check which vector ended up on which
    // chunk. The settings below make it answer as a broken or wrong model would.
    private sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly Dictionary<string, float[]> _vectors = [];
        private readonly List<IReadOnlyList<string>> _requests = [];

        // The texts of each call, in the order they were sent.
        public IReadOnlyList<IReadOnlyList<string>> Requests => _requests;

        public IEnumerable<string> Texts => _requests.SelectMany(request => request);

        public int Dimensions { get; set; } = ChunkConfiguration.EmbeddingDimensions;

        // That many vectors fewer than texts are returned.
        public int VectorsLeftOut { get; set; }

        // Thrown by every call, as an unreachable model would.
        public Exception? Failure { get; set; }

        // Calls from this one on (1-based) fail; earlier ones succeed.
        public int? FailFromRequest { get; set; }

        public Action? OnRequest { get; set; }

        public float[] VectorOf(string text) => _vectors[text];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var texts = values.ToList();
            _requests.Add(texts);
            OnRequest?.Invoke();

            if (Failure is not null)
            {
                throw Failure;
            }

            if (_requests.Count >= FailFromRequest)
            {
                throw new HttpRequestException("Connection lost.");
            }

            var embeddings = texts
                .Take(texts.Count - VectorsLeftOut)
                .Select(text => new Embedding<float>(VectorFor(text)));

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private float[] VectorFor(string text)
        {
            if (!_vectors.TryGetValue(text, out var vector))
            {
                vector = new float[Dimensions];
                vector[0] = _vectors.Count + 1;
                _vectors[text] = vector;
            }

            return vector;
        }
    }
}
