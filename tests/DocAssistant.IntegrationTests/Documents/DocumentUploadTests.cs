using System.Net;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Documents;

// POST /documents: who may upload, which files are accepted, and what a successful
// upload leaves behind (docs/decisions.md #32, #33, #37). Each test registers its own
// organization, so tests never see each other's documents.
[Collection(DatabaseCollection.Name)]
public class DocumentUploadTests(PostgresFixture database)
{
    // ---- Who may upload (decisions #32) -----------------------------------------------

    [Fact]
    public async Task UploadWithoutATokenIsUnauthorized()
    {
        await using var api = UploadTestApi.Start(database);

        var response = await api.UploadAsync(token: null, UploadFiles.Pdf(), "ik.pdf");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(api.Storage.Files);
    }

    // Admins upload, members ask questions.
    [Fact]
    public async Task MemberCannotUpload()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var response = await api.UploadAsync(api.MemberToken(admin.TenantId), UploadFiles.Pdf(), "ik.pdf");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(api.Storage.Files);
    }

    // ---- Which files are accepted (decisions #32) -------------------------------------

    [Fact]
    public async Task RequestWithoutAFileIsABadRequest()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var form = new MultipartFormDataContent { { new StringContent("ik.pdf"), "title" } };

        var response = await api.SendAsync(HttpMethod.Post, "/documents", admin.Token, form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EmptyFileIsABadRequest()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var response = await api.UploadAsync(admin.Token, [], "ik.pdf");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(api.Storage.Files);
    }

    [Fact]
    public async Task FileLargerThan20MegabytesIsRejected()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var tooLarge = new byte[DocumentEndpoints.MaxFileSizeBytes + 1];
        UploadFiles.Pdf().CopyTo(tooLarge, 0);

        var response = await api.UploadAsync(admin.Token, tooLarge, "ik.pdf");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(api.Storage.Files);
    }

    [Fact]
    public async Task FileOfExactly20MegabytesIsAccepted()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var largest = new byte[DocumentEndpoints.MaxFileSizeBytes];
        UploadFiles.Pdf().CopyTo(largest, 0);

        var response = await api.UploadAsync(admin.Token, largest, "ik.pdf");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // The type comes from the bytes; the name and the Content-Type header are whatever
    // the client says they are.
    [Theory]
    [InlineData("text", "notlar.pdf")]
    [InlineData("zip", "tablo.docx")]
    public async Task FileThatIsNeitherPdfNorDocxIsUnsupported(string kind, string fileName)
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var content = kind == "text" ? UploadFiles.Text() : UploadFiles.ZipWithoutWordDocument();

        var response = await api.UploadAsync(admin.Token, content, fileName);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(api.Storage.Files);
    }

    [Fact]
    public async Task PdfNamedLikeAWordFileIsStoredAsPdf()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var document = await UploadAndReadAsync(api, admin, UploadFiles.Pdf(), "rapor.docx");

        Assert.Equal(DocumentContentTypes.Pdf, document.ContentType);
        Assert.EndsWith("/original.pdf", (await ReadStoredAsync(document.Id)).StorageKey);
    }

    // ---- A successful upload -----------------------------------------------------------

    // decisions #32: the endpoint does not wait for processing.
    [Fact]
    public async Task UploadIsAcceptedAsPendingWithALinkToItsStatus()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var content = UploadFiles.Pdf();

        var response = await api.UploadAsync(admin.Token, content, "ik-yonetmeligi.pdf");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.Equal($"/documents/{document!.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(nameof(DocumentStatus.Pending), document.Status);
        Assert.Equal("ik-yonetmeligi", document.Title);
        Assert.Equal("ik-yonetmeligi.pdf", document.FileName);
        Assert.Equal(DocumentContentTypes.Pdf, document.ContentType);
        Assert.Equal(content.Length, document.SizeBytes);
        Assert.Null(document.FailureReason);
        Assert.Null(document.ProcessedAt);
    }

    // decisions #33: the path is built from ids only, with the tenant first.
    [Fact]
    public async Task FileIsStoredUnderItsTenantAndDocument()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var content = UploadFiles.Pdf();

        var document = await UploadAndReadAsync(api, admin, content, "ik-yonetmeligi.pdf");

        var expectedKey = $"tenants/{admin.TenantId}/documents/{document.Id}/original.pdf";
        var stored = Assert.Single(api.Storage.Files);
        Assert.Equal(expectedKey, stored.Key);
        Assert.Equal(content, stored.Value.Content);
        Assert.Equal(DocumentContentTypes.Pdf, stored.Value.ContentType);
        Assert.Equal(expectedKey, (await ReadStoredAsync(document.Id)).StorageKey);
    }

    [Fact]
    public async Task DocxIsStoredAsDocx()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var document = await UploadAndReadAsync(api, admin, UploadFiles.Docx(), "ik-yonetmeligi.docx");

        Assert.Equal(DocumentContentTypes.Docx, document.ContentType);
        Assert.EndsWith("/original.docx", Assert.Single(api.Storage.Files).Key);
    }

    // decisions #31: the worker has no request to read the tenant from, so it travels
    // with the document id.
    [Fact]
    public async Task DocumentIsQueuedWithItsTenant()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var document = await UploadAndReadAsync(api, admin, UploadFiles.Pdf(), "ik.pdf");

        Assert.Equal(new IngestionWorkItem(admin.TenantId, document.Id), Assert.Single(api.QueuedItems));
    }

    // Tenant and uploader come from the token only, never from the request.
    [Fact]
    public async Task DocumentIsRecordedForTheUploadersTenant()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var content = UploadFiles.Pdf();

        var document = await UploadAndReadAsync(api, admin, content, "ik.pdf");

        var stored = await ReadStoredAsync(document.Id);
        Assert.Equal(admin.TenantId, stored.TenantId);
        Assert.Equal(admin.UserId, stored.UploadedByUserId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(content.Length, stored.SizeBytes);
        Assert.Equal(1, stored.Version);
        Assert.Equal(0, stored.AttemptCount);
    }

    // Some clients send a full path as the file name. Only the name is kept, and it never
    // reaches the storage path (decisions #33).
    [Fact]
    public async Task FileNameIsCleanedAndKeptOutOfTheStoragePath()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var document = await UploadAndReadAsync(api, admin, UploadFiles.Pdf(), @"C:\Belgeler\Gizli\izin-kurallari.pdf");

        Assert.Equal("izin-kurallari.pdf", document.FileName);
        Assert.Equal("izin-kurallari", document.Title);

        var key = Assert.Single(api.Storage.Files).Key;
        Assert.DoesNotContain("Belgeler", key);
        Assert.DoesNotContain("izin-kurallari", key);
    }

    // The storage key and the tenant id stay inside the API.
    [Fact]
    public async Task ResponseDoesNotRevealTheStorageKeyOrTenant()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var response = await api.UploadAsync(admin.Token, UploadFiles.Pdf(), "ik.pdf");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenants/", json);
        Assert.DoesNotContain(admin.TenantId.ToString(), json);
    }

    private static async Task<DocumentResponse> UploadAndReadAsync(
        UploadTestApi api,
        TestAdmin admin,
        byte[] content,
        string fileName)
    {
        var response = await api.UploadAsync(admin.Token, content, fileName);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<DocumentResponse>())!;
    }

    // The stored row, read as the superuser with filters off (docs/testing.md rule 2).
    private async Task<Document> ReadStoredAsync(Guid documentId)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Documents.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == documentId);
    }
}
