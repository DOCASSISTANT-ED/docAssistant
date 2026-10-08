using System.Net;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Documents;

// GET /documents/{id}: how a client follows processing after an upload. Any member of the
// tenant may look; another tenant's document does not exist for the caller (query filter
// and RLS, decisions #14, #15).
[Collection(DatabaseCollection.Name)]
public class DocumentStatusTests(PostgresFixture database)
{
    [Fact]
    public async Task UploaderSeesTheDocument()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var uploaded = await UploadAsync(api, admin);

        var response = await api.GetDocumentAsync(admin.Token, uploaded.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(uploaded, await response.Content.ReadFromJsonAsync<DocumentResponse>());
    }

    // Members cannot upload, but they may follow their organization's documents.
    [Fact]
    public async Task MemberOfTheSameTenantSeesTheDocument()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var uploaded = await UploadAsync(api, admin);

        var response = await api.GetDocumentAsync(api.MemberToken(admin.TenantId), uploaded.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // 404, not 403: the caller must not even learn that the id exists elsewhere.
    [Fact]
    public async Task AnotherTenantsDocumentIsNotFound()
    {
        await using var api = UploadTestApi.Start(database);
        var owner = await api.RegisterAdminAsync();
        var outsider = await api.RegisterAdminAsync();
        var uploaded = await UploadAsync(api, owner);

        var response = await api.GetDocumentAsync(outsider.Token, uploaded.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownDocumentIsNotFound()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var response = await api.GetDocumentAsync(admin.Token, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StatusWithoutATokenIsUnauthorized()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var uploaded = await UploadAsync(api, admin);

        var response = await api.GetDocumentAsync(token: null, uploaded.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The point of the endpoint: what the worker writes later shows up here, including
    // the reason a document failed (decisions #24).
    [Fact]
    public async Task ShowsTheCurrentStatusAndTheFailureReason()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var uploaded = await UploadAsync(api, admin);
        const string reason = "Belgede okunabilir metin bulunamadı.";

        await using (var db = database.CreateOwnerDbContext())
        {
            await db.Documents.IgnoreQueryFilters()
                .Where(d => d.Id == uploaded.Id)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(d => d.Status, DocumentStatus.Failed)
                    .SetProperty(d => d.FailureReason, reason));
        }

        var document = await api.GetDocumentAsync(admin.Token, uploaded.Id);

        var body = await document.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.Equal(nameof(DocumentStatus.Failed), body!.Status);
        Assert.Equal(reason, body.FailureReason);
    }

    private static async Task<DocumentResponse> UploadAsync(UploadTestApi api, TestAdmin admin)
    {
        var response = await api.UploadAsync(admin.Token, UploadFiles.Pdf(), "ik.pdf");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<DocumentResponse>())!;
    }
}
