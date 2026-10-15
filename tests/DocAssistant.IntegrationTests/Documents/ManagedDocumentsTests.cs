using DocAssistant.Api.Modules.Documents;
using DocAssistant.IntegrationTests.Infrastructure;
using DocAssistant.IntegrationTests.Ingestion;

namespace DocAssistant.IntegrationTests.Documents;

// Checks the document management test setup itself: a prepared document is in the asked
// state, has its chunks and its stored file. When this fails, the listing and deleting
// tests fail for the same reason.
[Collection(DatabaseCollection.Name)]
public class ManagedDocumentsTests(PostgresFixture database)
{
    [Theory]
    [InlineData(DocumentStatus.Pending)]
    [InlineData(DocumentStatus.Processing)]
    [InlineData(DocumentStatus.Done)]
    [InlineData(DocumentStatus.Failed)]
    public async Task APreparedDocumentHasItsStateItsChunksAndItsFile(DocumentStatus status)
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var document = await ManagedDocuments.PrepareAsync(api, database, admin, status, chunkCount: 3);

        var stored = await IngestionTestSupport.ReadAsync(database, document.Id);
        Assert.Equal(status, stored.Status);
        Assert.Equal(admin.TenantId, stored.TenantId);
        Assert.Equal(3, await ManagedDocuments.ChunkCountAsync(database, document.Id));
        Assert.True(api.Storage.Files.ContainsKey(stored.StorageKey));
    }

    [Fact]
    public async Task AStorageToldToFailDeletesKeepsTheFileAndRecordsTheAttempt()
    {
        var storage = new InMemoryFileStorage { FailDeletes = true };
        await using var api = UploadTestApi.Start(database, storage);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.UploadAsync(api, admin.Token);
        var key = await ManagedDocuments.StorageKeyAsync(database, document.Id);

        await Assert.ThrowsAsync<IOException>(() => storage.DeleteAsync(key));

        Assert.True(storage.Files.ContainsKey(key));
        Assert.Contains(key, storage.DeletedKeys);
    }
}
