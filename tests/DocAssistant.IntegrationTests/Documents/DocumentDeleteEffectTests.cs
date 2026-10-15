using System.Net;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.IntegrationTests.Infrastructure;

namespace DocAssistant.IntegrationTests.Documents;

// What a successful delete takes away and what it leaves (docs/decisions.md #66): the
// document's row, its chunks (they go with the row, #29) and its stored file go; the
// organization's other documents stay as they were. Afterwards the document is gone from
// every place that showed it.
[Collection(DatabaseCollection.Name)]
public class DocumentDeleteEffectTests(PostgresFixture database)
{
    [Fact]
    public async Task TheRowTheChunksAndTheStoredFileGo()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done, chunkCount: 3);
        var key = await ManagedDocuments.StorageKeyAsync(database, document.Id);

        using var response = await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await ManagedDocuments.RowExistsAsync(database, document.Id));
        Assert.Equal(0, await ManagedDocuments.ChunkCountAsync(database, document.Id));
        Assert.False(api.Storage.Files.ContainsKey(key));
        Assert.Equal([key], api.Storage.DeletedKeys);
    }

    [Fact]
    public async Task TheOrganizationsOtherDocumentsStayAsTheyWere()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var kept = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done, chunkCount: 2, "kalan.pdf");
        var deleted = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done, chunkCount: 2, "silinen.pdf");
        var keptKey = await ManagedDocuments.StorageKeyAsync(database, kept.Id);

        using var response = await ManagedDocuments.DeleteAsync(api, admin.Token, deleted.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await ManagedDocuments.RowExistsAsync(database, kept.Id));
        Assert.Equal(2, await ManagedDocuments.ChunkCountAsync(database, kept.Id));
        Assert.True(api.Storage.Files.ContainsKey(keptKey));
        Assert.DoesNotContain(keptKey, api.Storage.DeletedKeys);
    }

    [Fact]
    public async Task ADeletedDocumentIsNoLongerListed()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var kept = await ManagedDocuments.UploadAsync(api, admin.Token, "kalan.pdf");
        var deleted = await ManagedDocuments.UploadAsync(api, admin.Token, "silinen.pdf");

        await ManagedDocuments.DeleteAsync(api, admin.Token, deleted.Id);

        Assert.Equal([kept.Id], (await ManagedDocuments.ListedAsync(api, admin.Token)).Select(d => d.Id));
    }

    // Everything that showed the document: its state, its file and the text of its places
    // (the panel beside the chat, #63).
    [Theory]
    [InlineData("")]
    [InlineData("/file")]
    [InlineData("/passages?sectionPath=B%C3%B6l%C3%BCm%201&pageNumber=1")]
    public async Task ADeletedDocumentIsNotFoundAnyMore(string path)
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done);

        using (var before = await api.SendAsync(HttpMethod.Get, $"/documents/{document.Id}{path}", admin.Token, content: null))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        using var after = await api.SendAsync(HttpMethod.Get, $"/documents/{document.Id}{path}", admin.Token, content: null);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    // Removing an outdated document and uploading its new form is what an admin will do.
    [Fact]
    public async Task TheSameFileCanBeUploadedAgainAfterItWasDeleted()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var first = await ManagedDocuments.UploadAsync(api, admin.Token, "ik-yonetmeligi.pdf");
        var firstKey = await ManagedDocuments.StorageKeyAsync(database, first.Id);
        await ManagedDocuments.DeleteAsync(api, admin.Token, first.Id);

        var second = await ManagedDocuments.UploadAsync(api, admin.Token, "ik-yonetmeligi.pdf");

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(firstKey, await ManagedDocuments.StorageKeyAsync(database, second.Id));
        Assert.Equal([second.Id], (await ManagedDocuments.ListedAsync(api, admin.Token)).Select(d => d.Id));
        Assert.Equal("ik-yonetmeligi.pdf", (await ManagedDocuments.ListedAsync(api, admin.Token))[0].FileName);
    }
}
