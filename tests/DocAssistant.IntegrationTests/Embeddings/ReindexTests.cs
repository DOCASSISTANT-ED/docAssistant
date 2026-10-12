using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Embeddings;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.IntegrationTests.Infrastructure;
using DocAssistant.IntegrationTests.Ingestion;
using DocAssistant.IntegrationTests.TenantIsolation;
using DocAssistant.Shared.Tenancy;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;

namespace DocAssistant.IntegrationTests.Embeddings;

// Re-indexing (docs/decisions.md #54): chunks without an embedding, or with one made by
// another model than the configured one, are embedded again when the app starts. Three
// parts are tested in turn: the database function that finds the documents across all
// tenants, the reindexer that handles one document, and the service that runs at startup.
//
// Everything runs as in the app (restricted database user, the scope's tenant, query
// filter, RLS). Only the embedding model is replaced by one that gives every text a vector
// of its own, so a stored vector can be traced back to the text it was made from.
//
// The scenario's documents: A1 and A2 of tenant A, B1 of tenant B.
[Collection(DatabaseCollection.Name)]
public class ReindexTests(PostgresFixture database) : IAsyncLifetime
{
    private const string OlderModel = "an-older-model";

    private TenantIsolationScenario _scenario = null!;
    private string _currentModel = null!;
    private int _nextOrdinal;

    public async Task InitializeAsync()
    {
        await IngestionTestSupport.WaitForStartupRecoveryAsync(database);
        _scenario = await TenantIsolationScenario.CreateAsync(database);
        _currentModel = database.Api.Services.GetRequiredService<IOptions<EmbeddingOptions>>().Value.Model;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- The database function: which documents need re-indexing ----

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, OlderModel)]
    [InlineData(true, OlderModel)]
    [InlineData(true, null)]
    public async Task DocumentWithAChunkLackingACurrentEmbeddingIsListed(bool hasVector, string? model)
    {
        await AddChunkAsync(_scenario.DocumentA1, hasVector ? AnyVector() : null, model);

        Assert.Contains(new ReindexItem(_scenario.TenantA, _scenario.DocumentA1), await DocumentsToReindexAsync());
    }

    // A vector is missing although the model name says it is current: still not searchable.
    [Fact]
    public async Task ChunkNamingTheCurrentModelButWithoutAVectorIsListed()
    {
        await AddChunkAsync(_scenario.DocumentA1, vector: null, _currentModel);

        Assert.Contains(_scenario.DocumentA1, (await DocumentsToReindexAsync()).Select(item => item.DocumentId));
    }

    [Fact]
    public async Task DocumentsThatAreUpToDateOrHaveNoChunksAreNotListed()
    {
        await AddChunkAsync(_scenario.DocumentA1, AnyVector(), _currentModel);
        await AddChunkAsync(_scenario.DocumentA1, AnyVector(), _currentModel);

        var listed = (await DocumentsToReindexAsync()).Select(item => item.DocumentId).ToList();

        Assert.DoesNotContain(_scenario.DocumentA1, listed);
        Assert.DoesNotContain(_scenario.DocumentA2, listed);
    }

    [Fact]
    public async Task DocumentIsListedOnceHoweverManyChunksAreOutdated()
    {
        await AddChunkAsync(_scenario.DocumentA1, AnyVector(), _currentModel);
        await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null);
        await AddChunkAsync(_scenario.DocumentA1, AnyVector(), OlderModel);

        var listed = (await DocumentsToReindexAsync()).Where(item => item.DocumentId == _scenario.DocumentA1);

        Assert.Single(listed);
    }

    // "Current" is whatever model the caller names: after a change of model every chunk
    // made with the old one is outdated.
    [Fact]
    public async Task ChangingTheModelMakesItsChunksOutdated()
    {
        await AddChunkAsync(_scenario.DocumentA1, AnyVector(), _currentModel);

        Assert.DoesNotContain(_scenario.DocumentA1, (await DocumentsToReindexAsync(_currentModel)).Select(item => item.DocumentId));
        Assert.Contains(_scenario.DocumentA1, (await DocumentsToReindexAsync("a-newer-model")).Select(item => item.DocumentId));
    }

    // The app has no tenant selected at startup and no superuser connection (decisions #19).
    [Fact]
    public async Task DocumentsOfEveryTenantAreListedWithTheirTenantWithoutATenantSelected()
    {
        await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null);
        await AddChunkAsync(_scenario.DocumentB1, vector: null, model: null, _scenario.TenantB);

        var listed = await DocumentsToReindexAsync();

        Assert.Contains(new ReindexItem(_scenario.TenantA, _scenario.DocumentA1), listed);
        Assert.Contains(new ReindexItem(_scenario.TenantB, _scenario.DocumentB1), listed);
    }

    // Why the function exists: the same connection cannot see the rows themselves.
    [Fact]
    public async Task SameConnectionSeesNoChunkRowsDirectly()
    {
        await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null);

        await using var db = database.CreateAppDbContext(tenantId: null);

        Assert.Equal(0, await db.Chunks.IgnoreQueryFilters().CountAsync());
    }

    // The function reads across tenants, so it must not be open to every database role.
    [Fact]
    public async Task FunctionCanBeCalledByTheAppUserButNotByEveryone()
    {
        await using var db = database.CreateOwnerDbContext();

        var appUserMayCall = await db.Database
            .SqlQuery<bool>($"""SELECT has_function_privilege('docassistant_app', 'documents_to_reindex(text)', 'EXECUTE') AS "Value" """)
            .SingleAsync();
        var everyoneMayCall = await db.Database
            .SqlQuery<bool>($"""SELECT has_function_privilege('public', 'documents_to_reindex(text)', 'EXECUTE') AS "Value" """)
            .SingleAsync();

        Assert.True(appUserMayCall);
        Assert.False(everyoneMayCall);
    }

    // ---- The reindexer: one document ----

    [Fact]
    public async Task OutdatedChunksGetAVectorOfTheirOwnTextAndTheCurrentModel()
    {
        var model = new FakeModel();
        var never = await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null);
        var older = await AddChunkAsync(_scenario.DocumentA1, AnyVector(), OlderModel);
        var unnamed = await AddChunkAsync(_scenario.DocumentA1, AnyVector(), model: null);
        var lost = await AddChunkAsync(_scenario.DocumentA1, vector: null, _currentModel);

        var count = await ReindexAsync(_scenario.TenantA, _scenario.DocumentA1, model);

        Assert.Equal(4, count);
        var chunks = await ReadChunksAsync(_scenario.DocumentA1);
        Assert.Equal([never, older, unnamed, lost], chunks.Select(chunk => chunk.Id));
        Assert.All(chunks, chunk =>
        {
            Assert.Equal(_currentModel, chunk.EmbeddingModel);
            Assert.Equal(model.VectorOf(chunk.Content), chunk.Embedding);
        });
    }

    [Fact]
    public async Task ChunksThatAreUpToDateAreNotEmbeddedAgain()
    {
        var model = new FakeModel();
        var kept = AnyVector();
        var current = await AddChunkAsync(_scenario.DocumentA1, kept, _currentModel);
        var outdated = await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null);

        var count = await ReindexAsync(_scenario.TenantA, _scenario.DocumentA1, model);

        Assert.Equal(1, count);
        var chunks = (await ReadChunksAsync(_scenario.DocumentA1)).ToDictionary(chunk => chunk.Id);
        Assert.Equal(kept, chunks[current].Embedding);
        Assert.Equal([chunks[outdated].Content], model.Asked);
    }

    // decisions #54: only the vector and the model's name change.
    [Fact]
    public async Task TextOrderAndIdentityOfTheChunksStayAsTheyWere()
    {
        var before = new List<Chunk>();
        for (var i = 0; i < 3; i++)
        {
            await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null);
        }

        before.AddRange(await ReadChunksAsync(_scenario.DocumentA1));

        await ReindexAsync(_scenario.TenantA, _scenario.DocumentA1, new FakeModel());

        var after = await ReadChunksAsync(_scenario.DocumentA1);
        Assert.Equal(
            before.Select(chunk => (chunk.Id, chunk.Ordinal, chunk.Content, chunk.SectionPath, chunk.PageNumber)),
            after.Select(chunk => (chunk.Id, chunk.Ordinal, chunk.Content, chunk.SectionPath, chunk.PageNumber)));
    }

    // The whole content goes to the model, context header included (decisions #50).
    [Fact]
    public async Task WholeContentIsEmbeddedInDocumentOrder()
    {
        var model = new FakeModel();
        const string first = "Belge: İK Yönetmeliği > Bölüm 2: Yıllık İzin\n\nİzin süresi kıdeme göre belirlenir.";
        const string second = "Belge: İK Yönetmeliği > Bölüm 3: Harcırah\n\nGünlük 1.500 TL harcırah ödenir.";
        await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null, content: first);
        await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null, content: second);

        await ReindexAsync(_scenario.TenantA, _scenario.DocumentA1, model);

        Assert.Equal([first, second], model.Asked);
    }

    [Fact]
    public async Task NothingOutdatedMeansNoCallToTheModel()
    {
        var model = new FakeModel();
        await AddChunkAsync(_scenario.DocumentA1, AnyVector(), _currentModel);

        Assert.Equal(0, await ReindexAsync(_scenario.TenantA, _scenario.DocumentA1, model));
        Assert.Equal(0, await ReindexAsync(_scenario.TenantA, _scenario.DocumentA2, model));
        Assert.Empty(model.Asked);
    }

    // Texts go to the model 32 at a time, as when a document is first processed (decisions #50).
    [Fact]
    public async Task LargeDocumentIsEmbeddedInSeveralRequests()
    {
        var model = new FakeModel();
        await AddChunksAsync(_scenario.DocumentA1, 70);

        var count = await ReindexAsync(_scenario.TenantA, _scenario.DocumentA1, model);

        Assert.Equal(70, count);
        Assert.Equal([32, 32, 6], model.Requests);
        Assert.All(await ReadChunksAsync(_scenario.DocumentA1), chunk => Assert.Equal(model.VectorOf(chunk.Content), chunk.Embedding));
    }

    // All of a document's chunks or none: a half re-indexed document would be searchable
    // in parts.
    [Fact]
    public async Task ModelFailingPartWayLeavesEveryChunkAsItWas()
    {
        var model = new FakeModel { FailFromRequest = 2 };
        await AddChunksAsync(_scenario.DocumentA1, 40);

        await Assert.ThrowsAsync<HttpRequestException>(() => ReindexAsync(_scenario.TenantA, _scenario.DocumentA1, model));

        Assert.All(await ReadChunksAsync(_scenario.DocumentA1), AssertOutdated);
    }

    // A model of another size is a configuration mistake and says so (decisions #54).
    [Theory]
    [InlineData(768)]
    [InlineData(1025)]
    public async Task VectorOfAnotherSizeIsRejectedAndNothingIsWritten(int dimensions)
    {
        var model = new FakeModel { Dimensions = dimensions };
        await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReindexAsync(_scenario.TenantA, _scenario.DocumentA1, model));

        Assert.Contains(dimensions.ToString(), error.Message);
        Assert.All(await ReadChunksAsync(_scenario.DocumentA1), AssertOutdated);
    }

    [Fact]
    public async Task OnlyTheNamedDocumentIsTouched()
    {
        await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null);
        await AddChunkAsync(_scenario.DocumentA2, vector: null, model: null);
        await AddChunkAsync(_scenario.DocumentB1, vector: null, model: null, _scenario.TenantB);

        await ReindexAsync(_scenario.TenantA, _scenario.DocumentA1, new FakeModel());

        Assert.All(await ReadChunksAsync(_scenario.DocumentA1), AssertCurrent);
        Assert.All(await ReadChunksAsync(_scenario.DocumentA2), AssertOutdated);
        Assert.All(await ReadChunksAsync(_scenario.DocumentB1), AssertOutdated);
    }

    // decisions #31: each document is handled with its own tenant selected.
    [Fact]
    public async Task DocumentOfAnotherTenantIsReindexedWithThatTenant()
    {
        await AddChunkAsync(_scenario.DocumentB1, vector: null, model: null, _scenario.TenantB);

        var count = await ReindexAsync(_scenario.TenantB, _scenario.DocumentB1, new FakeModel());

        Assert.Equal(1, count);
        Assert.All(await ReadChunksAsync(_scenario.DocumentB1), AssertCurrent);
    }

    // A job that names the wrong tenant must not reach another tenant's chunks.
    [Fact]
    public async Task JobNamingAnotherTenantCannotTouchTheDocument()
    {
        var model = new FakeModel();
        await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null);

        var count = await ReindexAsync(_scenario.TenantB, _scenario.DocumentA1, model);

        Assert.Equal(0, count);
        Assert.Empty(model.Asked);
        Assert.All(await ReadChunksAsync(_scenario.DocumentA1), AssertOutdated);
    }

    // ---- The service: what happens at startup ----
    //
    // The database is shared by all tests and never reset, so a run also re-indexes what
    // other tests left behind. These tests look only at their own documents.

    [Fact]
    public async Task StartupReindexesTheOutdatedChunksOfEveryTenant()
    {
        var model = new FakeModel();
        var kept = AnyVector();
        var current = await AddChunkAsync(_scenario.DocumentA1, kept, _currentModel);
        await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null);
        await AddChunkAsync(_scenario.DocumentA2, AnyVector(), OlderModel);
        await AddChunkAsync(_scenario.DocumentB1, vector: null, model: null, _scenario.TenantB);

        await RunServiceAsync(model);

        foreach (var document in new[] { _scenario.DocumentA1, _scenario.DocumentA2, _scenario.DocumentB1 })
        {
            Assert.All(await ReadChunksAsync(document), chunk =>
            {
                AssertCurrent(chunk);
                Assert.Equal(chunk.Id == current ? kept : model.VectorOf(chunk.Content), chunk.Embedding);
            });
        }

        Assert.DoesNotContain(_scenario.DocumentA1, (await DocumentsToReindexAsync()).Select(item => item.DocumentId));
    }

    [Fact]
    public async Task SwitchedOffNothingIsReindexed()
    {
        var model = new FakeModel();
        await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null);

        await RunServiceAsync(model, enabled: false);

        Assert.Empty(model.Asked);
        Assert.All(await ReadChunksAsync(_scenario.DocumentA1), AssertOutdated);
    }

    // The app must come up even when the model is down: keyword search still works.
    [Fact]
    public async Task UnusableModelEndsTheRunWithoutAnErrorAndLeavesTheChunksOutdated()
    {
        var model = new FakeModel { FailFromRequest = 1 };
        await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null);

        var log = await RunServiceAsync(model);

        Assert.All(await ReadChunksAsync(_scenario.DocumentA1), AssertOutdated);
        Assert.Contains(LogLevel.Error, log.Levels);
    }

    // decisions #54: with the model unusable the run stops, since the documents after it
    // would fail the same way; what was finished stays, and the next start goes on from there.
    [Fact]
    public async Task RunStopsAtTheDocumentTheModelFailsOnAndTheNextStartContinues()
    {
        const string poison = "Bu chunk'ta model hata verir.";
        var documents = new[] { _scenario.DocumentA1, _scenario.DocumentA2, _scenario.DocumentB1 };
        await AddChunkAsync(_scenario.DocumentA1, vector: null, model: null);
        await AddChunkAsync(_scenario.DocumentA2, vector: null, model: null);
        await AddChunkAsync(_scenario.DocumentB1, vector: null, model: null, _scenario.TenantB);

        // The order the service will take them in; the middle one gets the text the model fails on.
        var order = (await DocumentsToReindexAsync()).Select(item => item.DocumentId).Where(documents.Contains).ToList();
        await SetContentAsync(order[1], poison);

        await RunServiceAsync(new FakeModel { FailOn = poison });

        Assert.All(await ReadChunksAsync(order[0]), AssertCurrent);
        Assert.All(await ReadChunksAsync(order[1]), AssertOutdated);
        Assert.All(await ReadChunksAsync(order[2]), AssertOutdated);

        // The model works again at the next start.
        await RunServiceAsync(new FakeModel());

        var remaining = (await DocumentsToReindexAsync()).Select(item => item.DocumentId).ToList();
        Assert.All(order, document => Assert.DoesNotContain(document, remaining));
    }

    // ---- Helpers ----

    private static Vector AnyVector()
    {
        var values = new float[ChunkConfiguration.EmbeddingDimensions];
        values[5] = 1;

        return new Vector(values);
    }

    private void AssertCurrent(Chunk chunk)
    {
        Assert.NotNull(chunk.Embedding);
        Assert.Equal(_currentModel, chunk.EmbeddingModel);
    }

    private void AssertOutdated(Chunk chunk) =>
        Assert.True(chunk.Embedding is null || chunk.EmbeddingModel != _currentModel, "The chunk was re-indexed.");

    // One chunk written as the superuser: the tests are about re-indexing, not about how
    // chunks get there. Every chunk gets a text of its own.
    private async Task<Guid> AddChunkAsync(
        Guid documentId, Vector? vector, string? model, Guid? tenantId = null, string? content = null)
    {
        await using var db = database.CreateOwnerDbContext(tenantId ?? _scenario.TenantA);

        var chunk = NewChunk(documentId, vector, model, content);
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        return chunk.Id;
    }

    private async Task AddChunksAsync(Guid documentId, int count)
    {
        await using var db = database.CreateOwnerDbContext(_scenario.TenantA);

        db.Chunks.AddRange(Enumerable.Range(0, count).Select(_ => NewChunk(documentId, vector: null, model: null, content: null)));
        await db.SaveChangesAsync();
    }

    private Chunk NewChunk(Guid documentId, Vector? vector, string? model, string? content)
    {
        var ordinal = _nextOrdinal++;

        return new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = documentId,
            DocumentVersion = 1,
            Ordinal = ordinal,
            Content = content ?? $"Belge {documentId:N}, {ordinal}. parça.",
            SourceType = DocumentSourceType.Docx,
            Embedding = vector,
            EmbeddingModel = model,
        };
    }

    private async Task SetContentAsync(Guid documentId, string content)
    {
        await using var db = database.CreateOwnerDbContext();

        await db.Chunks.IgnoreQueryFilters()
            .Where(chunk => chunk.DocumentId == documentId)
            .ExecuteUpdateAsync(set => set.SetProperty(chunk => chunk.Content, content));
    }

    // The stored truth, read as the superuser with filters off, in document order.
    private async Task<List<Chunk>> ReadChunksAsync(Guid documentId)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Chunks.IgnoreQueryFilters().AsNoTracking()
            .Where(chunk => chunk.DocumentId == documentId)
            .OrderBy(chunk => chunk.Ordinal)
            .ToListAsync();
    }

    // The function as the service calls it: restricted user, no tenant selected.
    private async Task<List<ReindexItem>> DocumentsToReindexAsync(string? model = null)
    {
        await using var db = database.CreateAppDbContext(tenantId: null);
        var current = model ?? _currentModel;

        return await db.Database
            .SqlQuery<ReindexItem>($"SELECT tenant_id, document_id FROM documents_to_reindex({current})")
            .ToListAsync();
    }

    // One document in its own scope, as the service does it; only the model is replaced.
    private async Task<int> ReindexAsync(Guid tenantId, Guid documentId, FakeModel model)
    {
        await using var scope = database.Api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var reindexer = new ChunkReindexer(
            services.GetRequiredService<AppDbContext>(),
            services.GetRequiredService<ITenantSelector>(),
            model,
            services.GetRequiredService<IOptions<EmbeddingOptions>>());

        return await reindexer.ReindexAsync(new ReindexItem(tenantId, documentId), CancellationToken.None);
    }

    // The service run to its end against a copy of the app that has the fake model. The
    // copy's own startup work is taken out: its recovery and worker would pick up other
    // tests' unfinished documents (see IngestionTestSupport), and its own re-indexing is
    // off in tests; the service is started here instead, so the test can wait for it.
    private async Task<RecordingLogger> RunServiceAsync(FakeModel model, bool enabled = true)
    {
        await using var api = database.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(model);

            var startupWork = services
                .Where(service => service.ServiceType == typeof(IHostedService)
                    && (service.ImplementationType == typeof(IngestionRecoveryService)
                        || service.ImplementationType == typeof(IngestionWorker)))
                .ToList();

            foreach (var service in startupWork)
            {
                services.Remove(service);
            }
        }));

        var configured = api.Services.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
        var options = Options.Create(new EmbeddingOptions
        {
            BaseUrl = configured.BaseUrl,
            Model = configured.Model,
            ReindexOnStartup = enabled,
        });

        var log = new RecordingLogger();
        using var service = new ReindexService(api.Services.GetRequiredService<IServiceScopeFactory>(), options, log);

        await service.StartAsync(CancellationToken.None);
        await (service.ExecuteTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(120));
        await service.StopAsync(CancellationToken.None);

        return log;
    }

    // Stands in for the embedding model: every text gets a vector of its own, so a stored
    // vector tells which text it was made from. Records what it was asked.
    private sealed class FakeModel : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly Dictionary<string, int> _numbers = [];
        private readonly List<string> _asked = [];
        private readonly List<int> _requests = [];

        public IReadOnlyList<string> Asked => _asked;

        // How many texts each request carried.
        public IReadOnlyList<int> Requests => _requests;

        public int Dimensions { get; init; } = ChunkConfiguration.EmbeddingDimensions;

        // The request (counted from 1) from which the model cannot be reached; 0 = never.
        public int FailFromRequest { get; init; }

        // A text the model fails on.
        public string? FailOn { get; init; }

        public Vector VectorOf(string text) => new(Values(text, ChunkConfiguration.EmbeddingDimensions));

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var texts = values.ToList();
            _requests.Add(texts.Count);

            if ((FailFromRequest > 0 && _requests.Count >= FailFromRequest) || (FailOn is not null && texts.Contains(FailOn)))
            {
                throw new HttpRequestException("The embedding model cannot be reached.");
            }

            _asked.AddRange(texts);

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                texts.Select(text => new Embedding<float>(Values(text, Dimensions)))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private float[] Values(string text, int dimensions)
        {
            if (!_numbers.TryGetValue(text, out var number))
            {
                number = _numbers.Count + 1;
                _numbers[text] = number;
            }

            var vector = new float[dimensions];
            vector[0] = number;
            vector[1] = 1;

            return vector;
        }
    }

    private sealed class RecordingLogger : ILogger<ReindexService>
    {
        private readonly List<LogLevel> _levels = [];

        public IReadOnlyList<LogLevel> Levels
        {
            get
            {
                lock (_levels)
                {
                    return _levels.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_levels)
            {
                _levels.Add(logLevel);
            }
        }
    }
}
