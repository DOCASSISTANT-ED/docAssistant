using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Documents.Storage;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.IntegrationTests.Documents;
using DocAssistant.IntegrationTests.Infrastructure;
using DocAssistant.IntegrationTests.TenantIsolation;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocAssistant.IntegrationTests.Ingestion;

// The real processing step: read the file, parse, chunk, store (docs/decisions.md #27,
// #29, #35, #42, #43), and the first exit criterion of phase 2: an uploaded PDF or DOCX
// ends up as chunks with metadata in the database.
//
// Each test handles one queue message the way the worker does (real handler, restricted
// database user, real tenant selector, query filters, RLS) with the real processor,
// parsers and chunker. Only the file storage is replaced by one in memory, and the clock
// by a fixed one.
[Collection(DatabaseCollection.Name)]
public class DocumentProcessorTests(PostgresFixture database) : IAsyncLifetime
{
    // The sample documents open with this heading (tests/TestData/README.md).
    private const string Title = "İK Yönetmeliği";

    private const string SampleDocx = "ik-yonetmeligi.docx";
    private const string SamplePdf = "ik-yonetmeligi.pdf";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 30, 0, TimeSpan.Zero);

    // One chunk per section of the sample document; every section is well under the limit.
    private static readonly string?[] SampleSectionPaths =
    [
        null,
        "Bölüm 1: Çalışma Saatleri",
        "Bölüm 1: Çalışma Saatleri > 1.1 Fazla Mesai",
        "Bölüm 2: Yıllık İzin",
        "Bölüm 3: Harcırah",
    ];

    private readonly InMemoryFileStorage _storage = new();
    private TenantIsolationScenario _scenario = null!;

    public async Task InitializeAsync()
    {
        await IngestionTestSupport.WaitForStartupRecoveryAsync(database);
        _scenario = await TenantIsolationScenario.CreateAsync(database);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- A document becomes chunks ----

    [Theory]
    [InlineData(SampleDocx, DocumentContentTypes.Docx, DocumentSourceType.Docx)]
    [InlineData(SamplePdf, DocumentContentTypes.Pdf, DocumentSourceType.Pdf)]
    public async Task UploadedDocumentIsStoredAsChunksWithMetadata(
        string fileName, string contentType, DocumentSourceType sourceType)
    {
        var documentId = await AddDocumentAsync(_scenario.TenantA, _scenario.Alice, contentType, Sample(fileName));

        await HandleAsync(_scenario.TenantA, documentId);

        var chunks = await ReadChunksAsync(documentId);

        Assert.Equal(SampleSectionPaths, chunks.Select(c => c.SectionPath));
        Assert.Equal([0, 1, 2, 3, 4], chunks.Select(c => c.Ordinal));
        Assert.All(chunks, chunk =>
        {
            Assert.Equal(_scenario.TenantA, chunk.TenantId);
            Assert.Equal(documentId, chunk.DocumentId);
            Assert.Equal(1, chunk.DocumentVersion);
            Assert.Equal(sourceType, chunk.SourceType);

            // decisions #28: filled in phase 3.
            Assert.Null(chunk.Embedding);
            Assert.Null(chunk.EmbeddingModel);
        });
    }

    // decisions #27: the header is part of the stored text, and the same for both formats.
    [Theory]
    [InlineData(SampleDocx, DocumentContentTypes.Docx)]
    [InlineData(SamplePdf, DocumentContentTypes.Pdf)]
    public async Task ChunkContentStartsWithTheContextHeader(string fileName, string contentType)
    {
        var documentId = await AddDocumentAsync(_scenario.TenantA, _scenario.Alice, contentType, Sample(fileName));

        await HandleAsync(_scenario.TenantA, documentId);

        var chunks = await ReadChunksAsync(documentId);

        Assert.StartsWith(
            "Belge: İK Yönetmeliği\n\nBu yönetmelik, Örnek Lojistik A.Ş.", chunks[0].Content);
        Assert.StartsWith(
            "Belge: İK Yönetmeliği > Bölüm 1: Çalışma Saatleri > 1.1 Fazla Mesai\n\nHaftalık kırk beş saati aşan",
            chunks[2].Content);
        Assert.Contains("1.500 TL", chunks[4].Content);
    }

    // decisions #22: a PDF has pages, a DOCX does not.
    [Fact]
    public async Task PdfChunksCarryThePageTheyStartOn()
    {
        var documentId = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, DocumentContentTypes.Pdf, Sample(SamplePdf));

        await HandleAsync(_scenario.TenantA, documentId);

        Assert.Equal([1, 1, 1, 1, 2], (await ReadChunksAsync(documentId)).Select(c => c.PageNumber));
    }

    [Fact]
    public async Task DocxChunksHaveNoPageNumber()
    {
        var documentId = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, DocumentContentTypes.Docx, Sample(SampleDocx));

        await HandleAsync(_scenario.TenantA, documentId);

        Assert.All(await ReadChunksAsync(documentId), chunk => Assert.Null(chunk.PageNumber));
    }

    // decisions #43: each table row is written with its column names.
    [Fact]
    public async Task DocxTableRowsAreStoredWithTheirColumnNames()
    {
        var documentId = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, DocumentContentTypes.Docx, Sample(SampleDocx));

        await HandleAsync(_scenario.TenantA, documentId);

        var annualLeave = (await ReadChunksAsync(documentId))[3];
        Assert.Contains(
            "Kıdem: 1–5 yıl; Yıllık izin günü: 14\n"
            + "Kıdem: 5–15 yıl; Yıllık izin günü: 20\n"
            + "Kıdem: 15 yıl ve üzeri; Yıllık izin günü: 26",
            annualLeave.Content);
    }

    [Fact]
    public async Task ProcessedDocumentIsDoneWithTheTimeItFinished()
    {
        var documentId = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, DocumentContentTypes.Docx, Sample(SampleDocx));

        await HandleAsync(_scenario.TenantA, documentId);

        var document = await IngestionTestSupport.ReadAsync(database, documentId);
        Assert.Equal(DocumentStatus.Done, document.Status);
        Assert.Equal(Now, document.ProcessedAt);
        Assert.Null(document.FailureReason);
        Assert.Equal(1, document.AttemptCount);
    }

    // ---- Documents that cannot be processed ----

    // decisions #42
    [Fact]
    public async Task DocumentWhoseFileIsMissingFailsAndAsksForANewUpload()
    {
        var documentId = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, DocumentContentTypes.Pdf, file: null);

        await HandleAsync(_scenario.TenantA, documentId);

        await AssertFailedAsync(documentId, DocumentProcessor.FileMissingMessage);
        Assert.Contains("yeniden yükleyin", DocumentProcessor.FileMissingMessage);
    }

    [Fact]
    public async Task DocumentOfAnUnsupportedTypeFails()
    {
        var documentId = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, "text/plain", "Düz metin."u8.ToArray());

        await HandleAsync(_scenario.TenantA, documentId);

        await AssertFailedAsync(documentId, DocumentProcessor.UnsupportedTypeMessage);
    }

    // decisions #24: what the parser says about the file reaches the user.
    [Theory]
    [InlineData(DocumentContentTypes.Pdf)]
    [InlineData(DocumentContentTypes.Docx)]
    public async Task FileThatCannotBeParsedFailsWithTheParsersReason(string contentType)
    {
        var documentId = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, contentType, "Bu bir belge dosyası değil."u8.ToArray());

        await HandleAsync(_scenario.TenantA, documentId);

        var document = await IngestionTestSupport.ReadAsync(database, documentId);
        Assert.Equal(DocumentStatus.Failed, document.Status);
        Assert.False(string.IsNullOrWhiteSpace(document.FailureReason));
        Assert.Null(document.ProcessedAt);
        Assert.Empty(await ReadChunksAsync(documentId));
    }

    // Headings only: the parser finds text, but there is nothing to search.
    [Fact]
    public async Task DocumentThatGivesNoChunksFails()
    {
        var documentId = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, DocumentContentTypes.Pdf, [1, 2, 3]);
        var parser = new StubParser(
            DocumentSourceType.Pdf,
            new HeadingBlock("Bölüm 1", 1, 1),
            new HeadingBlock("Bölüm 2", 1, 1));

        await HandleAsync(_scenario.TenantA, documentId, parser);

        await AssertFailedAsync(documentId, DocumentProcessor.NoTextMessage);
    }

    // ---- One set of chunks, all or nothing ----

    // decisions #36: a run that was cut off after its commit may have left chunks behind.
    [Fact]
    public async Task ProcessingAgainReplacesTheChunksOfAnEarlierRun()
    {
        var documentId = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, DocumentContentTypes.Docx, Sample(SampleDocx));
        await AddStaleChunksAsync(_scenario.TenantA, documentId, count: 8);

        await HandleAsync(_scenario.TenantA, documentId);

        var chunks = await ReadChunksAsync(documentId);
        Assert.Equal([0, 1, 2, 3, 4], chunks.Select(c => c.Ordinal));
        Assert.DoesNotContain(chunks, chunk => chunk.Content.StartsWith(StaleContent, StringComparison.Ordinal));
    }

    // decisions #35: old chunks out, new chunks in and Done happen together or not at all.
    // PostgreSQL rejects the NUL character in text, so the insert fails after the delete.
    [Fact]
    public async Task FailureWhileSavingLeavesTheDocumentAsItWas()
    {
        var documentId = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, DocumentContentTypes.Pdf, [1, 2, 3]);
        await AddStaleChunksAsync(_scenario.TenantA, documentId, count: 3);
        var parser = new StubParser(
            DocumentSourceType.Pdf,
            new ParagraphBlock("Kaydedilebilir bir paragraf.", 1),
            new HeadingBlock("Bölüm 1", 1, 1),
            new ParagraphBlock("Kaydedilemeyen \0 bir paragraf.", 1));

        await Assert.ThrowsAsync<DbUpdateException>(() => HandleAsync(_scenario.TenantA, documentId, parser));

        var chunks = await ReadChunksAsync(documentId);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.StartsWith(StaleContent, chunk.Content));

        // Not Done and not Failed: recovery will try again (decisions #38).
        var document = await IngestionTestSupport.ReadAsync(database, documentId);
        Assert.Equal(DocumentStatus.Processing, document.Status);
        Assert.Null(document.ProcessedAt);
    }

    [Fact]
    public async Task ProcessingOneDocumentLeavesTheChunksOfOthersAlone()
    {
        var first = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, DocumentContentTypes.Docx, Sample(SampleDocx));
        var second = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, DocumentContentTypes.Pdf, Sample(SamplePdf));

        await HandleAsync(_scenario.TenantA, first);
        await HandleAsync(_scenario.TenantA, second);

        Assert.Equal(5, (await ReadChunksAsync(first)).Count);
        Assert.Equal(5, (await ReadChunksAsync(second)).Count);
    }

    // ---- The chunks table ----

    // Checked as the restricted user (docs/testing.md rule 2): first through the query
    // filter, then with the filter off, where only Row-Level Security is left.
    [Fact]
    public async Task ChunksAreVisibleOnlyToTheirOwnTenant()
    {
        var documentId = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, DocumentContentTypes.Docx, Sample(SampleDocx));
        await HandleAsync(_scenario.TenantA, documentId);

        await using var asTenantA = database.CreateAppDbContext(_scenario.TenantA);
        await using var asTenantB = database.CreateAppDbContext(_scenario.TenantB);
        await using var withoutTenant = database.CreateAppDbContext(tenantId: null);

        Assert.Equal(5, await asTenantA.Chunks.CountAsync(c => c.DocumentId == documentId));
        Assert.Equal(0, await asTenantB.Chunks.CountAsync(c => c.DocumentId == documentId));
        Assert.Equal(0, await asTenantB.Chunks.IgnoreQueryFilters().CountAsync(c => c.DocumentId == documentId));
        Assert.Equal(0, await withoutTenant.Chunks.IgnoreQueryFilters().CountAsync(c => c.DocumentId == documentId));
    }

    // decisions #29
    [Fact]
    public async Task DeletingADocumentDeletesItsChunks()
    {
        var documentId = await AddDocumentAsync(
            _scenario.TenantA, _scenario.Alice, DocumentContentTypes.Docx, Sample(SampleDocx));
        await HandleAsync(_scenario.TenantA, documentId);

        await using (var db = database.CreateAppDbContext(_scenario.TenantA))
        {
            Assert.Equal(1, await db.Documents.Where(d => d.Id == documentId).ExecuteDeleteAsync());
        }

        Assert.Empty(await ReadChunksAsync(documentId));
    }

    // ---- Helpers ----

    private const string StaleContent = "stale chunk";

    private static byte[] Sample(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));

    // A Pending document as the upload endpoint leaves it. file: null = nothing in storage.
    private async Task<Guid> AddDocumentAsync(Guid tenantId, Guid uploaderId, string contentType, byte[]? file)
    {
        var document = TestDocuments.New(Title, uploaderId);
        document.Id = Guid.CreateVersion7();
        document.ContentType = contentType;

        if (file is not null)
        {
            _storage.Put(document.StorageKey, file);
        }

        await using var db = database.CreateOwnerDbContext(tenantId);
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        return document.Id;
    }

    private async Task AddStaleChunksAsync(Guid tenantId, Guid documentId, int count)
    {
        await using var db = database.CreateOwnerDbContext(tenantId);

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

    // The stored truth, read as the superuser with filters off.
    private async Task<List<Chunk>> ReadChunksAsync(Guid documentId)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Chunks.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.DocumentId == documentId)
            .OrderBy(c => c.Ordinal)
            .ToListAsync();
    }

    private async Task AssertFailedAsync(Guid documentId, string reason)
    {
        var document = await IngestionTestSupport.ReadAsync(database, documentId);

        Assert.Equal(DocumentStatus.Failed, document.Status);
        Assert.Equal(reason, document.FailureReason);
        Assert.Null(document.ProcessedAt);
        Assert.Empty(await ReadChunksAsync(documentId));
    }

    // One message, handled in its own scope, exactly as IngestionWorker does it. Passing
    // parsers replaces the real ones.
    private async Task HandleAsync(Guid tenantId, Guid documentId, params IDocumentParser[] parsers)
    {
        await using var scope = database.Api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var db = services.GetRequiredService<AppDbContext>();

        var processor = new DocumentProcessor(
            db,
            _storage,
            parsers.Length > 0 ? parsers : services.GetServices<IDocumentParser>(),
            new FixedTimeProvider(Now));

        var handler = new DocumentIngestionHandler(
            db,
            services.GetRequiredService<ITenantSelector>(),
            processor,
            NullLogger<DocumentIngestionHandler>.Instance);

        await handler.HandleAsync(new IngestionWorkItem(tenantId, documentId), CancellationToken.None);
    }

    // Behaves as IFileStorage promises: a seekable stream, FileNotFoundException for an
    // unknown key.
    private sealed class InMemoryFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = [];

        public void Put(string key, byte[] content) => _files[key] = content;

        public Task SaveAsync(
            string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            _files[key] = buffer.ToArray();
            return Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            _files.TryGetValue(key, out var content)
                ? Task.FromResult<Stream>(new MemoryStream(content, writable: false))
                : throw new FileNotFoundException($"Nothing is stored under '{key}'.");

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _files.Remove(key);
            return Task.CompletedTask;
        }
    }

    // Returns fixed blocks whatever the file holds.
    private sealed class StubParser(DocumentSourceType sourceType, params DocumentBlock[] blocks) : IDocumentParser
    {
        public DocumentSourceType SourceType => sourceType;

        public ParsedDocument Parse(Stream content) => new(blocks);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
