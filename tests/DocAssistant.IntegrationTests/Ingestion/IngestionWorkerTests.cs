using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.IntegrationTests.Infrastructure;
using DocAssistant.IntegrationTests.TenantIsolation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DocAssistant.IntegrationTests.Ingestion;

// The whole background path in a running app: queue -> worker -> handler -> processor,
// plus startup recovery. Each test starts its own copy of the API with only the
// processing step replaced (docs/testing.md rule 4), so the real recovery service and
// worker run against the test database.
//
// These tests wait for a background service, so they poll with a timeout.
[Collection(DatabaseCollection.Name)]
public class IngestionWorkerTests(PostgresFixture database) : IAsyncLifetime
{
    private TenantIsolationScenario _scenario = null!;

    public async Task InitializeAsync()
    {
        await IngestionTestSupport.WaitForStartupRecoveryAsync(database);
        _scenario = await TenantIsolationScenario.CreateAsync(database);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task QueuedDocumentIsProcessed()
    {
        await using var api = StartApi();
        var queue = api.Services.GetRequiredService<IIngestionQueue>();

        await queue.EnqueueAsync(_scenario.TenantA, _scenario.DocumentA1);

        await WaitForStatusAsync(_scenario.DocumentA1, DocumentStatus.Done);
        Assert.Equal(1, (await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1)).AttemptCount);
    }

    // Phase 2 exit criterion: after a restart, a document that was not finished is
    // processed again without anyone uploading or queueing it.
    [Theory]
    [InlineData(DocumentStatus.Pending)]
    [InlineData(DocumentStatus.Processing)]
    public async Task UnfinishedDocumentIsProcessedAfterARestart(DocumentStatus leftAs)
    {
        await IngestionTestSupport.SetStateAsync(database, _scenario.DocumentB1, leftAs);

        await using var api = StartApi();

        await WaitForStatusAsync(_scenario.DocumentB1, DocumentStatus.Done);
    }

    // decisions #39: documents are processed one at a time, so a document that blows up
    // must not stop the ones behind it (or the app).
    [Fact]
    public async Task DocumentThatFailsUnexpectedlyDoesNotStopTheWorker()
    {
        await using var api = StartApi(failFor: _scenario.DocumentA1);
        var queue = api.Services.GetRequiredService<IIngestionQueue>();

        await queue.EnqueueAsync(_scenario.TenantA, _scenario.DocumentA1);
        await queue.EnqueueAsync(_scenario.TenantA, _scenario.DocumentA2);

        await WaitForStatusAsync(_scenario.DocumentA2, DocumentStatus.Done);

        var failed = await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1);
        Assert.Equal(DocumentStatus.Processing, failed.Status);
        Assert.True(failed.AttemptCount >= 1);
    }

    // decisions #36: the same document can be queued by both upload and recovery.
    [Fact]
    public async Task DocumentQueuedTwiceIsProcessedOnce()
    {
        await using var api = StartApi();
        var queue = api.Services.GetRequiredService<IIngestionQueue>();

        await queue.EnqueueAsync(_scenario.TenantA, _scenario.DocumentA1);
        await queue.EnqueueAsync(_scenario.TenantA, _scenario.DocumentA1);
        // A marker behind the duplicates: once it is Done, both of them have been handled.
        await queue.EnqueueAsync(_scenario.TenantA, _scenario.DocumentA2);

        await WaitForStatusAsync(_scenario.DocumentA2, DocumentStatus.Done);

        var document = await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1);
        Assert.Equal(DocumentStatus.Done, document.Status);
        Assert.Equal(1, document.AttemptCount);
    }

    // Documents of different tenants go through the same worker one after another; each
    // must be handled under its own tenant.
    [Fact]
    public async Task DocumentsOfDifferentTenantsAreEachProcessedUnderTheirOwnTenant()
    {
        await using var api = StartApi();
        var queue = api.Services.GetRequiredService<IIngestionQueue>();

        await queue.EnqueueAsync(_scenario.TenantA, _scenario.DocumentA1);
        await queue.EnqueueAsync(_scenario.TenantB, _scenario.DocumentB1);
        await queue.EnqueueAsync(_scenario.TenantA, _scenario.DocumentA2);

        await WaitForStatusAsync(_scenario.DocumentA1, DocumentStatus.Done);
        await WaitForStatusAsync(_scenario.DocumentB1, DocumentStatus.Done);
        await WaitForStatusAsync(_scenario.DocumentA2, DocumentStatus.Done);
    }

    // A copy of the API whose processing step just marks the document Done. Accessing
    // Services starts the host, and with it the recovery service and the worker.
    private WebApplicationFactory<Program> StartApi(Guid? failFor = null)
    {
        var api = database.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IDocumentProcessor>(sp =>
                new MarkDoneProcessor(sp.GetRequiredService<AppDbContext>(), failFor))));

        _ = api.Services;

        return api;
    }

    private Task WaitForStatusAsync(Guid documentId, DocumentStatus expected) =>
        IngestionTestSupport.WaitUntilAsync(
            async () => (await IngestionTestSupport.ReadAsync(database, documentId)).Status == expected,
            $"document {documentId} to become {expected}");

    private sealed class MarkDoneProcessor(AppDbContext db, Guid? failFor) : IDocumentProcessor
    {
        public async Task ProcessAsync(Document document, CancellationToken cancellationToken)
        {
            if (document.Id == failFor)
            {
                throw new InvalidOperationException("Simulated unexpected failure.");
            }

            // Written through the worker's own DbContext: this only succeeds if the
            // document's tenant is selected (write rules and RLS).
            document.Status = DocumentStatus.Done;
            document.ProcessedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
