using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.IntegrationTests.Infrastructure;
using DocAssistant.IntegrationTests.TenantIsolation;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocAssistant.IntegrationTests.Ingestion;

// What the worker does with one queue message (docs/decisions.md #31, #36, #38, #24):
// select the tenant, skip what is already finished, count the attempt, give up after
// three, and turn a parse failure into a Failed document with a reason for the user.
//
// The handler runs exactly as in the app (restricted database user, real tenant selector,
// query filters, RLS). Only the processing step is replaced, since reading and chunking
// the file is not what these tests are about.
[Collection(DatabaseCollection.Name)]
public class DocumentIngestionHandlerTests(PostgresFixture database) : IAsyncLifetime
{
    private TenantIsolationScenario _scenario = null!;

    public async Task InitializeAsync()
    {
        await IngestionTestSupport.WaitForStartupRecoveryAsync(database);
        _scenario = await TenantIsolationScenario.CreateAsync(database);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PendingDocumentIsHandedToTheProcessor()
    {
        var processor = new StubProcessor();

        await HandleAsync(_scenario.TenantA, _scenario.DocumentA1, processor);

        var call = Assert.Single(processor.Calls);
        Assert.Equal(_scenario.DocumentA1, call.DocumentId);
    }

    // The attempt is saved before processing starts, so one that crashes the app still counts.
    [Fact]
    public async Task AttemptIsCountedAndStatusIsProcessingBeforeProcessingStarts()
    {
        var processor = new StubProcessor();

        await HandleAsync(_scenario.TenantA, _scenario.DocumentA1, processor);

        var call = Assert.Single(processor.Calls);
        Assert.Equal(DocumentStatus.Processing, call.StoredStatus);
        Assert.Equal(1, call.StoredAttemptCount);
    }

    // decisions #31: without this the query filters, write rules and RLS would hide the
    // document from the worker and reject everything it writes.
    [Fact]
    public async Task TenantOfTheMessageIsSelectedWhileProcessing()
    {
        var processor = new StubProcessor();

        await HandleAsync(_scenario.TenantB, _scenario.DocumentB1, processor);

        var call = Assert.Single(processor.Calls);
        Assert.Equal(_scenario.TenantB, call.SelectedTenantId);
        Assert.Equal([_scenario.DocumentB1], call.DocumentsVisibleToTheProcessor);
    }

    [Fact]
    public async Task ProcessorCanFinishTheDocument()
    {
        var processor = new StubProcessor { Finish = true };

        await HandleAsync(_scenario.TenantA, _scenario.DocumentA1, processor);

        var document = await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1);
        Assert.Equal(DocumentStatus.Done, document.Status);
        Assert.Null(document.FailureReason);
    }

    // decisions #36: upload and recovery can both queue the same document.
    [Theory]
    [InlineData(DocumentStatus.Done)]
    [InlineData(DocumentStatus.Failed)]
    public async Task FinishedDocumentIsSkipped(DocumentStatus status)
    {
        await IngestionTestSupport.SetStateAsync(database, _scenario.DocumentA1, status, attemptCount: 1);
        var processor = new StubProcessor();

        await HandleAsync(_scenario.TenantA, _scenario.DocumentA1, processor);

        Assert.Empty(processor.Calls);
        var document = await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1);
        Assert.Equal(status, document.Status);
        Assert.Equal(1, document.AttemptCount);
    }

    // A document left Processing by a crash is picked up again (phase 2 exit criterion).
    [Fact]
    public async Task DocumentLeftProcessingIsProcessedAgain()
    {
        await IngestionTestSupport.SetStateAsync(database, _scenario.DocumentA1, DocumentStatus.Processing, attemptCount: 1);
        var processor = new StubProcessor { Finish = true };

        await HandleAsync(_scenario.TenantA, _scenario.DocumentA1, processor);

        Assert.Single(processor.Calls);
        var document = await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1);
        Assert.Equal(DocumentStatus.Done, document.Status);
        Assert.Equal(2, document.AttemptCount);
    }

    [Fact]
    public async Task MessageForAnUnknownDocumentIsIgnored()
    {
        var processor = new StubProcessor();

        var failure = await Record.ExceptionAsync(
            () => HandleAsync(_scenario.TenantA, Guid.NewGuid(), processor));

        Assert.Null(failure);
        Assert.Empty(processor.Calls);
    }

    // A message that names the wrong tenant must not reach another tenant's document.
    [Fact]
    public async Task MessageNamingAnotherTenantCannotTouchTheDocument()
    {
        var processor = new StubProcessor();

        await HandleAsync(_scenario.TenantB, _scenario.DocumentA1, processor);

        Assert.Empty(processor.Calls);
        var document = await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1);
        Assert.Equal(DocumentStatus.Pending, document.Status);
        Assert.Equal(0, document.AttemptCount);
    }

    // decisions #38: the third attempt is still made...
    [Fact]
    public async Task ThirdAttemptIsStillMade()
    {
        await IngestionTestSupport.SetStateAsync(database, _scenario.DocumentA1, DocumentStatus.Processing, attemptCount: 2);
        var processor = new StubProcessor();

        await HandleAsync(_scenario.TenantA, _scenario.DocumentA1, processor);

        Assert.Single(processor.Calls);
        Assert.Equal(3, (await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1)).AttemptCount);
    }

    // ...and after three the document fails for good, with a reason the user can read.
    [Fact]
    public async Task DocumentThatUsedUpItsAttemptsFailsWithoutBeingProcessed()
    {
        await IngestionTestSupport.SetStateAsync(
            database, _scenario.DocumentA1, DocumentStatus.Processing, attemptCount: DocumentIngestionHandler.MaxAttempts);
        var processor = new StubProcessor();

        await HandleAsync(_scenario.TenantA, _scenario.DocumentA1, processor);

        Assert.Empty(processor.Calls);
        var document = await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1);
        Assert.Equal(DocumentStatus.Failed, document.Status);
        Assert.Equal(DocumentIngestionHandler.TooManyAttemptsMessage, document.FailureReason);
        Assert.Equal(3, DocumentIngestionHandler.MaxAttempts);
    }

    // decisions #24: the parser's message is what the user will see.
    [Fact]
    public async Task ParseFailureMarksTheDocumentFailedWithTheParsersMessage()
    {
        const string reason = "Belgede okunabilir metin bulunamadı.";
        var processor = new StubProcessor { Throw = new DocumentParseException(reason) };

        var failure = await Record.ExceptionAsync(
            () => HandleAsync(_scenario.TenantA, _scenario.DocumentA1, processor));

        Assert.Null(failure);
        var document = await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1);
        Assert.Equal(DocumentStatus.Failed, document.Status);
        Assert.Equal(reason, document.FailureReason);
        Assert.Equal(1, document.AttemptCount);
    }

    // A file that cannot be parsed will not parse next time either: no retry.
    [Fact]
    public async Task DocumentFailedByAParseErrorIsNotRetried()
    {
        await HandleAsync(_scenario.TenantA, _scenario.DocumentA1,
            new StubProcessor { Throw = new DocumentParseException("Dosya okunamadı.") });
        var secondTime = new StubProcessor();

        await HandleAsync(_scenario.TenantA, _scenario.DocumentA1, secondTime);

        Assert.Empty(secondTime.Calls);
    }

    // decisions #35: a failed run must not leave part of its work behind.
    [Fact]
    public async Task UnsavedWorkOfAFailedParseIsNotWritten()
    {
        var processor = new StubProcessor
        {
            ChangeTitleTo = "half-finished work",
            Throw = new DocumentParseException("Dosya okunamadı."),
        };

        await HandleAsync(_scenario.TenantA, _scenario.DocumentA1, processor);

        var document = await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1);
        Assert.Equal("A-1", document.Title);
        Assert.Equal(DocumentStatus.Failed, document.Status);
    }

    // Anything else (storage down, a bug) may work next time: the error is passed on and
    // the document stays Processing, so recovery retries it up to the attempt limit.
    [Fact]
    public async Task UnexpectedErrorLeavesTheDocumentProcessingForARetry()
    {
        var processor = new StubProcessor { Throw = new InvalidOperationException("storage is down") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => HandleAsync(_scenario.TenantA, _scenario.DocumentA1, processor));

        var document = await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1);
        Assert.Equal(DocumentStatus.Processing, document.Status);
        Assert.Equal(1, document.AttemptCount);
        Assert.Null(document.FailureReason);
    }

    [Fact]
    public async Task HandlingOneTenantsDocumentLeavesOtherDocumentsAlone()
    {
        await HandleAsync(_scenario.TenantA, _scenario.DocumentA1, new StubProcessor { Finish = true });

        Assert.Equal(DocumentStatus.Pending, (await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA2)).Status);
        Assert.Equal(DocumentStatus.Pending, (await IngestionTestSupport.ReadAsync(database, _scenario.DocumentB1)).Status);
    }

    // One message, handled in its own scope, exactly as IngestionWorker does it.
    private async Task HandleAsync(Guid tenantId, Guid documentId, StubProcessor processor)
    {
        await using var scope = database.Api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var db = services.GetRequiredService<AppDbContext>();
        processor.Attach(database, db, services.GetRequiredService<ITenantContext>());

        var handler = new DocumentIngestionHandler(
            db,
            services.GetRequiredService<ITenantSelector>(),
            processor,
            NullLogger<DocumentIngestionHandler>.Instance);

        await handler.HandleAsync(new IngestionWorkItem(tenantId, documentId), CancellationToken.None);
    }

    // Stands in for "read the file, parse, chunk, store". Records what the handler had
    // done by the time it was called, then behaves as the test asks.
    private sealed class StubProcessor : IDocumentProcessor
    {
        private PostgresFixture _database = null!;
        private AppDbContext _db = null!;
        private ITenantContext _tenantContext = null!;

        public List<Call> Calls { get; } = [];

        public bool Finish { get; init; }

        public string? ChangeTitleTo { get; init; }

        public Exception? Throw { get; init; }

        public void Attach(PostgresFixture database, AppDbContext db, ITenantContext tenantContext)
        {
            _database = database;
            _db = db;
            _tenantContext = tenantContext;
        }

        public async Task ProcessAsync(Document document, CancellationToken cancellationToken)
        {
            var stored = await IngestionTestSupport.ReadAsync(_database, document.Id);

            Calls.Add(new Call(
                document.Id,
                stored.Status,
                stored.AttemptCount,
                _tenantContext.TenantId,
                await _db.Documents.AsNoTracking().Select(d => d.Id).ToListAsync(cancellationToken)));

            if (ChangeTitleTo is not null)
            {
                document.Title = ChangeTitleTo; // changed but deliberately not saved
            }

            if (Throw is not null)
            {
                throw Throw;
            }

            if (Finish)
            {
                document.Status = DocumentStatus.Done;
                document.ProcessedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private sealed record Call(
        Guid DocumentId,
        DocumentStatus StoredStatus,
        int StoredAttemptCount,
        Guid? SelectedTenantId,
        IReadOnlyList<Guid> DocumentsVisibleToTheProcessor);
}
