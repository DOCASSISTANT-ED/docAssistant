using System.Net;
using System.Text.Json;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Documents;

// GET /documents: the organization's documents, newest first (docs/decisions.md #66).
// Every member may look, since knowing which documents there are tells them what they
// can ask about; only the caller's own organization is listed.
[Collection(DatabaseCollection.Name)]
public class DocumentListTests(PostgresFixture database)
{
    [Fact]
    public async Task ANewOrganizationHasNoDocuments()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        using var response = await ManagedDocuments.ListAsync(api, admin.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ManagedDocuments.ListedAsync(api, admin.Token));
    }

    [Fact]
    public async Task TheListHasTheOrganizationsDocumentsNewestFirst()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var oldest = await ManagedDocuments.UploadAsync(api, admin.Token, "ik-yonetmeligi.pdf");
        var middle = await ManagedDocuments.UploadAsync(api, admin.Token, "satin-alma-proseduru.pdf");
        var newest = await ManagedDocuments.UploadAsync(api, admin.Token, "arac-kullanim-talimati.pdf");

        var listed = await ManagedDocuments.ListedAsync(api, admin.Token);

        Assert.Equal([newest.Id, middle.Id, oldest.Id], listed.Select(d => d.Id));
    }

    [Fact]
    public async Task AnotherOrganizationsDocumentsAreNotListed()
    {
        await using var api = UploadTestApi.Start(database);
        var ofA = await api.RegisterAdminAsync();
        var ofB = await api.RegisterAdminAsync();
        var documentOfA = await ManagedDocuments.UploadAsync(api, ofA.Token, "a-firmasi.pdf");
        var documentOfB = await ManagedDocuments.UploadAsync(api, ofB.Token, "b-firmasi.pdf");

        Assert.Equal([documentOfA.Id], (await ManagedDocuments.ListedAsync(api, ofA.Token)).Select(d => d.Id));
        Assert.Equal([documentOfB.Id], (await ManagedDocuments.ListedAsync(api, ofB.Token)).Select(d => d.Id));
    }

    // Only admins upload and delete (#32, #66); looking is for everyone.
    [Fact]
    public async Task AMemberWhoIsNotAnAdminSeesTheList()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.UploadAsync(api, admin.Token);

        var listed = await ManagedDocuments.ListedAsync(api, api.MemberToken(admin.TenantId));

        Assert.Equal([document.Id], listed.Select(d => d.Id));
    }

    [Fact]
    public async Task TheListNeedsASession()
    {
        await using var api = UploadTestApi.Start(database);

        using var response = await ManagedDocuments.ListAsync(api, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The decision lists what a document shows: name, type, size, state, the reason it
    // failed and dates. Where the file is stored and whose organization it belongs to stay
    // inside the API.
    [Fact]
    public async Task AnEntryHasTheDecidedFieldsAndNothingFromInsideTheApi()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        await ManagedDocuments.UploadAsync(api, admin.Token);

        using var response = await ManagedDocuments.ListAsync(api, admin.Token);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(
            ["contentType", "createdAt", "failureReason", "fileName", "id", "processedAt", "sizeBytes", "status", "title"],
            entry.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnEntryDescribesTheUploadedFile()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var uploaded = await ManagedDocuments.UploadAsync(api, admin.Token, "ik-yonetmeligi.pdf");

        var entry = Assert.Single(await ManagedDocuments.ListedAsync(api, admin.Token));

        Assert.Equal(uploaded.Id, entry.Id);
        Assert.Equal("ik-yonetmeligi.pdf", entry.FileName);
        Assert.Equal(uploaded.Title, entry.Title);
        Assert.Equal(DocumentContentTypes.Pdf, entry.ContentType);
        Assert.Equal(UploadFiles.Pdf().Length, entry.SizeBytes);
        Assert.Equal("Pending", entry.Status);
        Assert.Null(entry.FailureReason);
        Assert.Null(entry.ProcessedAt);
    }

    // Every state is listed, so an admin sees what is still being worked on and what failed.
    [Fact]
    public async Task DocumentsInEveryStateAreListedWithTheirState()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var pending = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Pending);
        var processing = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Processing);
        var done = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Done);
        var failed = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Failed);

        var listed = (await ManagedDocuments.ListedAsync(api, admin.Token)).ToDictionary(d => d.Id, d => d.Status);

        Assert.Equal("Pending", listed[pending.Id]);
        Assert.Equal("Processing", listed[processing.Id]);
        Assert.Equal("Done", listed[done.Id]);
        Assert.Equal("Failed", listed[failed.Id]);
    }

    // The reason is written for the user (#24): the list is where an admin reads it.
    [Fact]
    public async Task AFailedDocumentShowsWhyItFailed()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await ManagedDocuments.PrepareAsync(api, database, admin, DocumentStatus.Failed);
        await using (var db = database.CreateOwnerDbContext())
        {
            await db.Documents.IgnoreQueryFilters()
                .Where(d => d.Id == document.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(d => d.FailureReason, "Dosya parolalı olduğu için okunamadı."));
        }

        var entry = Assert.Single(await ManagedDocuments.ListedAsync(api, admin.Token));

        Assert.Equal("Failed", entry.Status);
        Assert.Equal("Dosya parolalı olduğu için okunamadı.", entry.FailureReason);
    }
}
