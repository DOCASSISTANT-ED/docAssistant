using System.Net;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.IntegrationTests.Infrastructure;

namespace DocAssistant.IntegrationTests.Documents;

// Who may delete a document (docs/decisions.md #66): admins of its own organization, as
// they upload (#32). Every refused delete leaves the document exactly as it was: its row,
// its chunks and its stored file. Another organization's document is "not found", like
// one that does not exist, so an id cannot be probed for being in use.
[Collection(DatabaseCollection.Name)]
public class DocumentDeleteAccessTests(PostgresFixture database)
{
    [Fact]
    public async Task AnAdminDeletesADocumentOfTheirOrganization()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done);

        using var response = await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.False(await ManagedDocuments.RowExistsAsync(database, document.Id));
    }

    [Fact]
    public async Task AMemberWhoIsNotAnAdminCannotDelete()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done);

        using var response = await ManagedDocuments.DeleteAsync(api, await api.MemberTokenAsync(admin.TenantId), document.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertUntouchedAsync(api, document);
    }

    [Fact]
    public async Task DeletingNeedsASession()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done);

        using var response = await ManagedDocuments.DeleteAsync(api, token: null, document.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertUntouchedAsync(api, document);
    }

    // The worst case: an admin of one organization removing another's document.
    [Fact]
    public async Task AnAdminOfAnotherOrganizationGetsNotFoundAndTheDocumentStays()
    {
        await using var api = UploadTestApi.Start(database);
        var owner = await api.RegisterAdminAsync();
        var stranger = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, owner, DocumentStatus.Done);

        using var response = await ManagedDocuments.DeleteAsync(api, stranger.Token, document.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertUntouchedAsync(api, document);
        Assert.Equal([document.Id], (await ManagedDocuments.ListedAsync(api, owner.Token)).Select(d => d.Id));
    }

    // The same answer as for another organization's document.
    [Fact]
    public async Task ADocumentThatDoesNotExistIsNotFound()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        using var response = await ManagedDocuments.DeleteAsync(api, admin.Token, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(api.Storage.DeletedKeys);
    }

    [Fact]
    public async Task AnotherOrganizationsDocumentLooksExactlyLikeOneThatDoesNotExist()
    {
        await using var api = UploadTestApi.Start(database);
        var owner = await api.RegisterAdminAsync();
        var stranger = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, owner, DocumentStatus.Done);

        using var existing = await ManagedDocuments.DeleteAsync(api, stranger.Token, document.Id);
        using var unknown = await ManagedDocuments.DeleteAsync(api, stranger.Token, Guid.NewGuid());

        Assert.Equal(unknown.StatusCode, existing.StatusCode);
        Assert.Equal(
            await unknown.Content.ReadAsStringAsync(),
            await existing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeletingTheSameDocumentTwiceGivesNotFoundTheSecondTime()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done);
        using var first = await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        using var second = await ManagedDocuments.DeleteAsync(api, admin.Token, document.Id);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    // The row, the chunks and the stored file are all still there.
    private async Task AssertUntouchedAsync(UploadTestApi api, DocumentResponse document)
    {
        Assert.True(await ManagedDocuments.RowExistsAsync(database, document.Id));
        Assert.Equal(2, await ManagedDocuments.ChunkCountAsync(database, document.Id));

        var key = await ManagedDocuments.StorageKeyAsync(database, document.Id);
        Assert.True(api.Storage.Files.ContainsKey(key));
        Assert.DoesNotContain(key, api.Storage.DeletedKeys);
    }
}
