using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.IntegrationTests.Infrastructure;

namespace DocAssistant.IntegrationTests.Documents;

// GET /documents/{id}/file: the uploaded file itself, opened from a source in the chat
// (docs/decisions.md #63). It is the first endpoint that hands a company's documents out,
// so who may get which file comes first. Files are kept in memory (UploadTestApi).
[Collection(DatabaseCollection.Name)]
public class DocumentFileTests(PostgresFixture database)
{
    // ---- Who may open which file ----------------------------------------------------

    [Fact]
    public async Task TheAdminWhoUploadedItCanOpenIt()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await UploadAsync(api, admin.Token, UploadFiles.Pdf(), "ik.pdf");

        var response = await GetFileAsync(api, admin.Token, document.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // decisions #63: whoever can ask about a document must be able to see what the answer
    // rests on. Uploading stays with admins (#32).
    [Fact]
    public async Task AMemberOfTheSameCompanyCanOpenIt()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await UploadAsync(api, admin.Token, UploadFiles.Pdf(), "ik.pdf");

        var response = await GetFileAsync(api, await api.MemberTokenAsync(admin.TenantId), document.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The answer must not tell another company that the id exists.
    [Fact]
    public async Task AnotherCompanyGetsNotFound()
    {
        await using var api = UploadTestApi.Start(database);
        var owner = await api.RegisterAdminAsync();
        var stranger = await api.RegisterAdminAsync();
        var document = await UploadAsync(api, owner.Token, UploadFiles.Pdf(), "ik.pdf");

        var response = await GetFileAsync(api, stranger.Token, document.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AMemberOfAnotherCompanyGetsNotFound()
    {
        await using var api = UploadTestApi.Start(database);
        var owner = await api.RegisterAdminAsync();
        var document = await UploadAsync(api, owner.Token, UploadFiles.Pdf(), "ik.pdf");

        var otherCompany = await api.RegisterAdminAsync();

        var response = await GetFileAsync(api, await api.MemberTokenAsync(otherCompany.TenantId), document.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ADocumentThatDoesNotExistIsNotFound()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var response = await GetFileAsync(api, admin.Token, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WithoutATokenTheRequestIsUnauthorized()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await UploadAsync(api, admin.Token, UploadFiles.Pdf(), "ik.pdf");

        var response = await GetFileAsync(api, token: null, document.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- What is sent -----------------------------------------------------------------

    [Fact]
    public async Task TheFileIsTheOneThatWasUploaded()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var content = UploadFiles.Docx();
        var document = await UploadAsync(api, admin.Token, content, "ik.docx");

        var response = await GetFileAsync(api, admin.Token, document.Id);

        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
    }

    // The type found in the bytes at upload (decisions #32), whatever the file was called.
    [Theory]
    [InlineData("ik.pdf", DocumentContentTypes.Pdf)]
    [InlineData("ik.docx", DocumentContentTypes.Docx)]
    public async Task TheTypeIsTheOneFoundAtUpload(string fileName, string contentType)
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var bytes = contentType == DocumentContentTypes.Pdf ? UploadFiles.Pdf() : UploadFiles.Docx();
        var document = await UploadAsync(api, admin.Token, bytes, fileName);

        var response = await GetFileAsync(api, admin.Token, document.Id);

        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
    }

    // The browser must not guess another type: an uploaded file never runs as a page.
    [Fact]
    public async Task TheBrowserIsToldNotToGuessTheType()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await UploadAsync(api, admin.Token, UploadFiles.Pdf(), "ik.pdf");

        var response = await GetFileAsync(api, admin.Token, document.Id);

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    // Company documents may be kept by the user's own browser, never by a shared cache.
    [Fact]
    public async Task NoSharedCacheMayKeepTheFile()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await UploadAsync(api, admin.Token, UploadFiles.Pdf(), "ik.pdf");

        var response = await GetFileAsync(api, admin.Token, document.Id);

        Assert.True(response.Headers.CacheControl?.Private);
        Assert.False(response.Headers.CacheControl?.Public);
    }

    // A PDF opens in the browser's own viewer, where the app can jump to the source's
    // page; a browser cannot show a DOCX, so it is downloaded.
    [Theory]
    [InlineData("ik.pdf", "inline")]
    [InlineData("ik.docx", "attachment")]
    public async Task APdfOpensInTheBrowserAndADocxIsDownloaded(string fileName, string disposition)
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var bytes = fileName.EndsWith(".pdf", StringComparison.Ordinal) ? UploadFiles.Pdf() : UploadFiles.Docx();
        var document = await UploadAsync(api, admin.Token, bytes, fileName);

        var response = await GetFileAsync(api, admin.Token, document.Id);

        Assert.Equal(disposition, response.Content.Headers.ContentDisposition?.DispositionType);
    }

    // filename* carries the name in UTF-8, so a saved copy keeps its Turkish letters.
    [Fact]
    public async Task TheFileKeepsItsNameTurkishLettersIncluded()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await UploadAsync(api, admin.Token, UploadFiles.Docx(), "İş Güvenliği Talimatı.docx");

        var response = await GetFileAsync(api, admin.Token, document.Id);

        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal(document.FileName, disposition.FileNameStar);
        Assert.Equal("İş Güvenliği Talimatı.docx", disposition.FileNameStar);
    }

    // A PDF viewer asks for the part it shows first instead of the whole file.
    [Fact]
    public async Task APartOfTheFileCanBeAskedFor()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var content = UploadFiles.Pdf();
        var document = await UploadAsync(api, admin.Token, content, "ik.pdf");

        var request = FileRequest(admin.Token, document.Id);
        request.Headers.Range = new RangeHeaderValue(0, 4);
        var response = await api.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(content[..5], await response.Content.ReadAsByteArrayAsync());
    }

    // ---- A file that is gone ------------------------------------------------------------

    // decisions #37: a row always has a file behind it. If something outside the app
    // removed the file, the document is gone for the user either way.
    [Fact]
    public async Task ADocumentWhoseFileIsMissingIsNotFound()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var document = await UploadAsync(api, admin.Token, UploadFiles.Pdf(), "ik.pdf");
        await api.Storage.DeleteAsync(Assert.Single(api.Storage.Files).Key);

        var response = await GetFileAsync(api, admin.Token, document.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static async Task<DocumentResponse> UploadAsync(UploadTestApi api, string token, byte[] content, string fileName)
    {
        var response = await api.UploadAsync(token, content, fileName);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<DocumentResponse>())!;
    }

    private static Task<HttpResponseMessage> GetFileAsync(UploadTestApi api, string? token, Guid documentId) =>
        api.Client.SendAsync(FileRequest(token, documentId));

    private static HttpRequestMessage FileRequest(string? token, Guid documentId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/documents/{documentId}/file");

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }
}
