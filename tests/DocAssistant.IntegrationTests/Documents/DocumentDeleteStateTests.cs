using System.Net;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.IntegrationTests.Infrastructure;
using DocAssistant.IntegrationTests.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DocAssistant.IntegrationTests.Documents;

// Deleting depending on how far processing has come (docs/decisions.md #66). A document
// being processed is not deleted: the background job would go on writing chunks for a row
// that is gone. One waiting to be processed may go: the job skips a document it cannot
// find. Done and failed documents are finished and may go.
[Collection(DatabaseCollection.Name)]
public class DocumentDeleteStateTests(PostgresFixture database)
{
    [Theory]
    [InlineData(DocumentStatus.Pending)]
    [InlineData(DocumentStatus.Done)]
    [InlineData(DocumentStatus.Failed)]
    public async Task ADocumentThatIsNotBeingProcessedIsDeleted(DocumentStatus status)
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, status);
        var key = await ManagedDocuments.StorageKeyAsync(database, document.Id);

        using var response = await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await ManagedDocuments.RowExistsAsync(database, document.Id));
        Assert.Equal(0, await ManagedDocuments.ChunkCountAsync(database, document.Id));
        Assert.False(api.Storage.Files.ContainsKey(key));
    }

    [Fact]
    public async Task ADocumentBeingProcessedIsNotDeleted()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Processing);
        var key = await ManagedDocuments.StorageKeyAsync(database, document.Id);

        using var response = await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(await ManagedDocuments.RowExistsAsync(database, document.Id));
        Assert.Equal(2, await ManagedDocuments.ChunkCountAsync(database, document.Id));
        Assert.True(api.Storage.Files.ContainsKey(key));
        Assert.DoesNotContain(key, api.Storage.DeletedKeys);
        Assert.Equal(DocumentStatus.Processing, (await IngestionTestSupport.ReadAsync(database, document.Id)).Status);
    }

    // A 409 the screen can show: the problem format every other error uses, with a reason.
    [Fact]
    public async Task TheRefusalSaysTheDocumentIsBeingProcessed()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Processing);

        using var response = await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(409, problem!.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
    }

    // "Try again in a moment": once processing has ended, the same request goes through.
    [Fact]
    public async Task TheSameDeleteGoesThroughOnceProcessingHasEnded()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Processing);
        using var refused = await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        await IngestionTestSupport.SetStateAsync(database, document.Id, DocumentStatus.Done);
        using var accepted = await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.False(await ManagedDocuments.RowExistsAsync(database, document.Id));
    }

    // A pending document's work item is still in the queue when it is deleted. When the
    // background job gets to it, the document is not found: the job skips it, writes no
    // chunks and does not bring the row back.
    [Fact]
    public async Task TheBackgroundJobSkipsAPendingDocumentThatWasDeleted()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.UploadAsync(api, admin.Token);
        var queued = Assert.Single(api.QueuedItems, item => item.DocumentId == document.Id);
        using var response = await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<DocumentIngestionHandler>();
            await handler.HandleAsync(queued, CancellationToken.None);
        }

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await ManagedDocuments.RowExistsAsync(database, document.Id));
        Assert.Equal(0, await ManagedDocuments.ChunkCountAsync(database, document.Id));
    }
}
