using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.IntegrationTests.Infrastructure;
using DocAssistant.IntegrationTests.TenantIsolation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocAssistant.IntegrationTests.Ingestion;

// Startup recovery (docs/decisions.md #34): the queue lives in memory, so after a restart
// the documents that were not finished have to be found again in the database, across
// all tenants, by an app that has no tenant selected and no superuser connection (#19).
//
// The scenario's three documents (two of tenant A, one of tenant B) start as Pending.
[Collection(DatabaseCollection.Name)]
public class IngestionRecoveryTests(PostgresFixture database) : IAsyncLifetime
{
    private TenantIsolationScenario _scenario = null!;

    public async Task InitializeAsync()
    {
        await IngestionTestSupport.WaitForStartupRecoveryAsync(database);
        _scenario = await TenantIsolationScenario.CreateAsync(database);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- the database function ---------------------------------------------------------

    [Fact]
    public async Task FunctionReturnsUnfinishedDocumentsOfEveryTenantWithoutATenantSelected()
    {
        var unfinished = await CallFunctionAsAppUserAsync();

        Assert.Contains(new IngestionWorkItem(_scenario.TenantA, _scenario.DocumentA1), unfinished);
        Assert.Contains(new IngestionWorkItem(_scenario.TenantA, _scenario.DocumentA2), unfinished);
        Assert.Contains(new IngestionWorkItem(_scenario.TenantB, _scenario.DocumentB1), unfinished);
    }

    // Why the function exists: the same connection cannot see the rows themselves.
    [Fact]
    public async Task SameConnectionSeesNoDocumentRowsDirectly()
    {
        await using var db = database.CreateAppDbContext(tenantId: null);

        var visible = await db.Documents.IgnoreQueryFilters().CountAsync();

        Assert.Equal(0, visible);
    }

    [Fact]
    public async Task FunctionIncludesProcessingAndLeavesOutDoneAndFailed()
    {
        await IngestionTestSupport.SetStateAsync(database, _scenario.DocumentA1, DocumentStatus.Processing);
        await IngestionTestSupport.SetStateAsync(database, _scenario.DocumentA2, DocumentStatus.Done);
        await IngestionTestSupport.SetStateAsync(database, _scenario.DocumentB1, DocumentStatus.Failed);

        var unfinished = (await CallFunctionAsAppUserAsync()).Select(item => item.DocumentId).ToList();

        Assert.Contains(_scenario.DocumentA1, unfinished);
        Assert.DoesNotContain(_scenario.DocumentA2, unfinished);
        Assert.DoesNotContain(_scenario.DocumentB1, unfinished);
    }

    // The function reads across tenants, so it must not be open to every database role.
    [Fact]
    public async Task FunctionCanBeCalledByTheAppUserButNotByEveryone()
    {
        await using var db = database.CreateOwnerDbContext();

        var appUserMayCall = await db.Database
            .SqlQuery<bool>($"""SELECT has_function_privilege('docassistant_app', 'unfinished_documents()', 'EXECUTE') AS "Value" """)
            .SingleAsync();

        // In an access list, a grant to PUBLIC is an entry with an empty role name: "=X/owner".
        var grantedToPublic = await db.Database
            .SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_proc, unnest(proacl) AS entry
                    WHERE proname = 'unfinished_documents' AND entry::text LIKE '=%'
                ) AS "Value"
                """)
            .SingleAsync();

        Assert.True(appUserMayCall);
        Assert.False(grantedToPublic);
    }

    // ---- the service that runs at startup ----------------------------------------------

    [Fact]
    public async Task ServiceQueuesEveryUnfinishedDocumentWithItsTenant()
    {
        await IngestionTestSupport.SetStateAsync(database, _scenario.DocumentA2, DocumentStatus.Processing);
        var queue = new RecordingQueue();

        await RunRecoveryAsync(queue);

        Assert.Contains(new IngestionWorkItem(_scenario.TenantA, _scenario.DocumentA1), queue.Items);
        Assert.Contains(new IngestionWorkItem(_scenario.TenantA, _scenario.DocumentA2), queue.Items);
        Assert.Contains(new IngestionWorkItem(_scenario.TenantB, _scenario.DocumentB1), queue.Items);
    }

    [Fact]
    public async Task ServiceDoesNotQueueFinishedDocuments()
    {
        await IngestionTestSupport.SetStateAsync(database, _scenario.DocumentA1, DocumentStatus.Done);
        await IngestionTestSupport.SetStateAsync(database, _scenario.DocumentB1, DocumentStatus.Failed);
        var queue = new RecordingQueue();

        await RunRecoveryAsync(queue);

        var queued = queue.Items.Select(item => item.DocumentId).ToList();
        Assert.DoesNotContain(_scenario.DocumentA1, queued);
        Assert.DoesNotContain(_scenario.DocumentB1, queued);
        Assert.Contains(_scenario.DocumentA2, queued);
    }

    [Fact]
    public async Task ServiceQueuesEachDocumentOnce()
    {
        var queue = new RecordingQueue();

        await RunRecoveryAsync(queue);

        Assert.Single(queue.Items, item => item.DocumentId == _scenario.DocumentA1);
    }

    // Recovery only reads: the documents are left exactly as they were.
    [Fact]
    public async Task ServiceDoesNotChangeTheDocuments()
    {
        await RunRecoveryAsync(new RecordingQueue());

        var document = await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1);
        Assert.Equal(DocumentStatus.Pending, document.Status);
        Assert.Equal(0, document.AttemptCount);
    }

    // A failure here must not take the whole API down; the documents stay unfinished in
    // the database and the next start tries again.
    [Fact]
    public async Task ServiceSurvivesAFailingQueue()
    {
        var failure = await Record.ExceptionAsync(() => RunRecoveryAsync(new BrokenQueue()));

        Assert.Null(failure);
        Assert.Equal(DocumentStatus.Pending, (await IngestionTestSupport.ReadAsync(database, _scenario.DocumentA1)).Status);
    }

    private async Task<List<IngestionWorkItem>> CallFunctionAsAppUserAsync()
    {
        await using var db = database.CreateAppDbContext(tenantId: null);

        return await db.Database
            .SqlQuery<IngestionWorkItem>($"SELECT tenant_id, document_id FROM unfinished_documents()")
            .ToListAsync();
    }

    // The real service, wired to the test API's services (restricted user, no tenant), with
    // only the queue replaced.
    private async Task RunRecoveryAsync(IIngestionQueue queue)
    {
        using var service = new IngestionRecoveryService(
            database.Api.Services.GetRequiredService<IServiceScopeFactory>(),
            queue,
            NullLogger<IngestionRecoveryService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await (service.ExecuteTask ?? Task.CompletedTask);
    }
}
