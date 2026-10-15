using System.Net;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Documents;

// GET /documents/{id}/passages: the text of a source, for the panel beside an answer
// (docs/decisions.md #63). A source names its place by section path and page (#56), so
// the chunks of that place are returned, in the document's order and in its own words.
//
// The document is uploaded through the API; its chunks are written here directly, so each
// test decides exactly which places the document has.
[Collection(DatabaseCollection.Name)]
public class DocumentPassagesTests(PostgresFixture database)
{
    private const string Section = "Bölüm 2: Yıllık İzin";

    // ---- Which text is returned ---------------------------------------------------------

    [Fact]
    public async Task ReturnsTheTextOfThePlaceInTheDocumentsOrder()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var documentId = await UploadAsync(api, admin.Token);
        await AddChunksAsync(admin.TenantId, documentId,
            (4, Section, 3, "Dördüncü."),
            (2, Section, 3, "İkinci."),
            (3, Section, 3, "Üçüncü."));

        var passages = await PassagesAsync(api, admin.Token, documentId, Section, 3);

        Assert.Equal(["İkinci.", "Üçüncü.", "Dördüncü."], passages.Select(p => p.Text));
        Assert.All(passages, passage => Assert.Equal(3, passage.PageNumber));
    }

    [Fact]
    public async Task LeavesOutTheOtherPlacesOfTheDocument()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var documentId = await UploadAsync(api, admin.Token);
        await AddChunksAsync(admin.TenantId, documentId,
            (0, Section, 3, "Aranan."),
            (1, Section, 4, "Aynı bölüm, başka sayfa."),
            (2, "Bölüm 3: Harcırah", 3, "Aynı sayfa, başka bölüm."),
            (3, null, 3, "Aynı sayfa, bölümsüz."));

        var passages = await PassagesAsync(api, admin.Token, documentId, Section, 3);

        Assert.Equal(["Aranan."], passages.Select(p => p.Text));
    }

    // decisions #27: the header is for search and the model; a reader gets the text.
    [Fact]
    public async Task ReturnsTheDocumentsOwnWordsWithoutTheContextHeader()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var documentId = await UploadAsync(api, admin.Token);
        await AddChunksAsync(admin.TenantId, documentId,
            (0, Section, 3, "Yıllık izin 14 gündür.\n\nTalep on gün önce yapılır."));

        var passage = Assert.Single(await PassagesAsync(api, admin.Token, documentId, Section, 3));

        Assert.Equal("Yıllık izin 14 gündür.\n\nTalep on gün önce yapılır.", passage.Text);
    }

    // decisions #22: a DOCX has no pages; its sources carry no page number.
    [Fact]
    public async Task FindsAPlaceWithoutAPage()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var documentId = await UploadAsync(api, admin.Token);
        await AddChunksAsync(admin.TenantId, documentId,
            (0, Section, null, "DOCX metni."),
            (1, Section, 1, "Sayfalı metin."));

        var passages = await PassagesAsync(api, admin.Token, documentId, Section, pageNumber: null);

        var passage = Assert.Single(passages);
        Assert.Equal("DOCX metni.", passage.Text);
        Assert.Null(passage.PageNumber);
    }

    // decisions #46: the text before the first heading has no section.
    [Fact]
    public async Task FindsAPlaceWithoutASection()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var documentId = await UploadAsync(api, admin.Token);
        await AddChunksAsync(admin.TenantId, documentId,
            (0, null, 1, "Giriş metni."),
            (1, Section, 1, "Bölümün metni."));

        var passages = await PassagesAsync(api, admin.Token, documentId, sectionPath: null, pageNumber: 1);

        Assert.Equal(["Giriş metni."], passages.Select(p => p.Text));
    }

    [Fact]
    public async Task FindsAPlaceWithNeitherSectionNorPage()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var documentId = await UploadAsync(api, admin.Token);
        await AddChunksAsync(admin.TenantId, documentId,
            (0, null, null, "Başlıksız DOCX metni."),
            (1, Section, null, "Bölümün metni."),
            (2, null, 1, "Sayfalı metin."));

        var passages = await PassagesAsync(api, admin.Token, documentId, sectionPath: null, pageNumber: null);

        Assert.Equal(["Başlıksız DOCX metni."], passages.Select(p => p.Text));
    }

    // "?sectionPath=" with nothing after it means no section, like leaving it out.
    [Fact]
    public async Task AnEmptySectionMeansNoSection()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var documentId = await UploadAsync(api, admin.Token);
        await AddChunksAsync(admin.TenantId, documentId,
            (0, null, 1, "Giriş metni."),
            (1, Section, 1, "Bölümün metni."));

        var response = await api.SendAsync(
            HttpMethod.Get, $"/documents/{documentId}/passages?sectionPath=&pageNumber=1", admin.Token, content: null);

        var passages = await response.Content.ReadFromJsonAsync<List<PassageResponse>>();
        Assert.Equal(["Giriş metni."], passages!.Select(p => p.Text));
    }

    // Section paths are written with ">" and Turkish letters; they must arrive intact.
    [Fact]
    public async Task FindsANestedSectionWithTurkishLetters()
    {
        const string nested = "Bölüm 4: İş Güvenliği > 4.1 Ağır Yük & Kaldırma";
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var documentId = await UploadAsync(api, admin.Token);
        await AddChunksAsync(admin.TenantId, documentId, (0, nested, 7, "25 kilogram."));

        var passages = await PassagesAsync(api, admin.Token, documentId, nested, 7);

        Assert.Equal(["25 kilogram."], passages.Select(p => p.Text));
    }

    // Chunks are replaced when a document is processed again (decisions #35); an older
    // version's chunks are not the document's text any more.
    [Fact]
    public async Task LeavesOutTheChunksOfAnotherVersion()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var documentId = await UploadAsync(api, admin.Token);
        await AddChunksAsync(admin.TenantId, documentId, (0, Section, 3, "Güncel."));
        await AddChunksAsync(admin.TenantId, documentId, version: 2, (1, Section, 3, "Başka sürüm."));

        var passages = await PassagesAsync(api, admin.Token, documentId, Section, 3);

        Assert.Equal(["Güncel."], passages.Select(p => p.Text));
    }

    // The document is there but no longer has that place (processed again, the section
    // renamed): an empty list, not an error.
    [Fact]
    public async Task APlaceTheDocumentNoLongerHasGivesAnEmptyList()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var documentId = await UploadAsync(api, admin.Token);
        await AddChunksAsync(admin.TenantId, documentId, (0, Section, 3, "Metin."));

        var response = await api.SendAsync(
            HttpMethod.Get, PassagesPath(documentId, "Bölüm 9: Kaldırılan", 3), admin.Token, content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<PassageResponse>>())!);
    }

    // ---- Who may read which document ------------------------------------------------------

    [Fact]
    public async Task AMemberOfTheSameCompanyCanReadIt()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var documentId = await UploadAsync(api, admin.Token);
        await AddChunksAsync(admin.TenantId, documentId, (0, Section, 3, "Metin."));

        var passages = await PassagesAsync(api, await api.MemberTokenAsync(admin.TenantId), documentId, Section, 3);

        Assert.Equal(["Metin."], passages.Select(p => p.Text));
    }

    // Not an empty list: that would tell another company the document exists.
    [Fact]
    public async Task AnotherCompanyGetsNotFound()
    {
        await using var api = UploadTestApi.Start(database);
        var owner = await api.RegisterAdminAsync();
        var stranger = await api.RegisterAdminAsync();
        var documentId = await UploadAsync(api, owner.Token);
        await AddChunksAsync(owner.TenantId, documentId, (0, Section, 3, "Gizli metin."));

        var response = await api.SendAsync(
            HttpMethod.Get, PassagesPath(documentId, Section, 3), stranger.Token, content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Gizli metin", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ADocumentThatDoesNotExistIsNotFound()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();

        var response = await api.SendAsync(
            HttpMethod.Get, PassagesPath(Guid.NewGuid(), Section, 3), admin.Token, content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WithoutATokenTheRequestIsUnauthorized()
    {
        await using var api = UploadTestApi.Start(database);
        var admin = await api.RegisterAdminAsync();
        var documentId = await UploadAsync(api, admin.Token);

        var response = await api.SendAsync(
            HttpMethod.Get, PassagesPath(documentId, Section, 3), token: null, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Helpers --------------------------------------------------------------------------

    private static async Task<Guid> UploadAsync(UploadTestApi api, string token)
    {
        var response = await api.UploadAsync(token, UploadFiles.Pdf(), "ik-yonetmeligi.pdf");
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<DocumentResponse>())!.Id;
    }

    private Task AddChunksAsync(
        Guid tenantId,
        Guid documentId,
        params (int Ordinal, string? SectionPath, int? PageNumber, string Text)[] chunks) =>
        AddChunksAsync(tenantId, documentId, version: 1, chunks);

    // Each chunk's content starts with a context header, as the chunker writes it
    // (decisions #27): "Belge: <title> > <section>", an empty line, then the text.
    private async Task AddChunksAsync(
        Guid tenantId,
        Guid documentId,
        int version,
        params (int Ordinal, string? SectionPath, int? PageNumber, string Text)[] chunks)
    {
        await using var db = database.CreateOwnerDbContext(tenantId);

        db.Chunks.AddRange(chunks.Select(chunk => new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = documentId,
            DocumentVersion = version,
            Ordinal = chunk.Ordinal,
            Content = chunk.SectionPath is null
                ? $"Belge: İK Yönetmeliği\n\n{chunk.Text}"
                : $"Belge: İK Yönetmeliği > {chunk.SectionPath}\n\n{chunk.Text}",
            SectionPath = chunk.SectionPath,
            PageNumber = chunk.PageNumber,
            SourceType = DocumentSourceType.Pdf,
        }));

        await db.SaveChangesAsync();
    }

    private static async Task<List<PassageResponse>> PassagesAsync(
        UploadTestApi api, string token, Guid documentId, string? sectionPath, int? pageNumber)
    {
        var response = await api.SendAsync(HttpMethod.Get, PassagesPath(documentId, sectionPath, pageNumber), token, content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<List<PassageResponse>>())!;
    }

    // A parameter that is null is left out, as the app does for a source without one.
    private static string PassagesPath(Guid documentId, string? sectionPath, int? pageNumber)
    {
        var query = new List<string>();

        if (sectionPath is not null)
        {
            query.Add($"sectionPath={Uri.EscapeDataString(sectionPath)}");
        }

        if (pageNumber is not null)
        {
            query.Add($"pageNumber={pageNumber}");
        }

        return query.Count == 0
            ? $"/documents/{documentId}/passages"
            : $"/documents/{documentId}/passages?{string.Join("&", query)}";
    }
}
