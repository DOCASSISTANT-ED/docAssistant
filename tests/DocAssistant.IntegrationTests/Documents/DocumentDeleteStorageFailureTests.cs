using System.Net;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.IntegrationTests.Infrastructure;

namespace DocAssistant.IntegrationTests.Documents;

// When the stored file cannot be deleted (docs/decisions.md #66). The row goes first, the
// reverse of the upload (#37): a file without a row is only wasted space, a row without
// its file would be a broken document. So the document counts as deleted even when the
// storage fails afterwards; only the file is left behind.
[Collection(DatabaseCollection.Name)]
public class DocumentDeleteStorageFailureTests(PostgresFixture database)
{
    [Fact]
    public async Task TheDocumentIsDeletedEvenWhenItsFileCannotBe()
    {
        await using var api = UploadTestApi.Start(database, new InMemoryFileStorage { FailDeletes = true });
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done, chunkCount: 3);

        using var response = await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await ManagedDocuments.RowExistsAsync(database, document.Id));
        Assert.Equal(0, await ManagedDocuments.ChunkCountAsync(database, document.Id));
    }

    // The leftover: the file was asked to go, could not, and is still in the storage.
    [Fact]
    public async Task OnlyTheFileIsLeftBehind()
    {
        await using var api = UploadTestApi.Start(database, new InMemoryFileStorage { FailDeletes = true });
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done);
        var key = await ManagedDocuments.StorageKeyAsync(database, document.Id);

        await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        Assert.Equal([key], api.Storage.DeletedKeys);
        Assert.True(api.Storage.Files.ContainsKey(key));
    }

    // For the user nothing of the document is left: the list, its state, its file and its
    // text all say it is gone, although the bytes are still in the storage.
    [Theory]
    [InlineData("")]
    [InlineData("/file")]
    [InlineData("/passages?sectionPath=B%C3%B6l%C3%BCm%201&pageNumber=1")]
    public async Task TheDocumentIsGoneForTheUser(string path)
    {
        await using var api = UploadTestApi.Start(database, new InMemoryFileStorage { FailDeletes = true });
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done);

        await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        using var response = await api.SendAsync(HttpMethod.Get, $"/documents/{document.Id}{path}", admin.Token, content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ManagedDocuments.ListedAsync(api, admin.Token));
    }
}
