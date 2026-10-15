using System.Net;
using System.Net.Http.Json;
using System.Text;
using DocAssistant.Api.Modules.Chat;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.Api.Modules.Search;
using DocAssistant.IntegrationTests.Chat;
using DocAssistant.IntegrationTests.Infrastructure;

namespace DocAssistant.IntegrationTests.Documents;

// A deleted document in old conversations (docs/decisions.md #66). The answers keep their
// sources: they are stored with the message and not tied to the document (#57), so the
// history still reads the same. Opening such a source finds nothing: its text and its file
// are gone (#63), and the chat screen says the document is no longer there.
[Collection(DatabaseCollection.Name)]
public class DocumentDeleteConversationTests(PostgresFixture database)
{
    private const string Section = "Bölüm 3: Harcırah";

    [Fact]
    public async Task AnOldAnswerKeepsTheSourceOfADeletedDocument()
    {
        await using var api = ChatTestApi.Start(database);
        var admin = await api.RegisterAsync();
        var documentId = await StoreDocumentAsync(api, admin);
        var answer = await AskAboutAsync(api, admin, documentId);

        using var deleted = await DeleteAsync(api, admin, documentId);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var conversation = await GetConversationAsync(api, admin, answer.Done.ConversationId);
        var source = Assert.Single(conversation.Messages[1].Sources);
        Assert.Equal(documentId, source.DocumentId);
        Assert.Equal("İK Yönetmeliği", source.DocumentTitle);
        Assert.Equal(Section, source.SectionPath);
        Assert.Equal(2, source.PageNumber);
        Assert.Equal("Günlük harcırah 1.500 TL'dir [1].", conversation.Messages[1].Content);
    }

    // What the panel beside the chat asks for when such a source is opened: before the
    // delete there is text and a file, after it both are "not found".
    [Theory]
    [InlineData("/passages?sectionPath=B%C3%B6l%C3%BCm%203%3A%20Harc%C4%B1rah&pageNumber=2")]
    [InlineData("/file")]
    [InlineData("")]
    public async Task OpeningTheSourceOfADeletedDocumentFindsNothing(string path)
    {
        await using var api = ChatTestApi.Start(database);
        var admin = await api.RegisterAsync();
        var documentId = await StoreDocumentAsync(api, admin);
        await AskAboutAsync(api, admin, documentId);

        using (var before = await api.GetAsync(admin.Token, $"/documents/{documentId}{path}"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using var deleted = await DeleteAsync(api, admin, documentId);
        using var after = await api.GetAsync(admin.Token, $"/documents/{documentId}{path}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    // A processed document as the chat would find it: a row, one chunk at the source's
    // place and the stored file.
    private async Task<Guid> StoreDocumentAsync(ChatTestApi api, TestUser admin)
    {
        var document = TestDocuments.New("İK Yönetmeliği", admin.UserId, admin.TenantId);
        document.Id = Guid.CreateVersion7();
        document.Status = Api.Modules.Documents.DocumentStatus.Done;

        await using (var db = database.CreateOwnerDbContext(admin.TenantId))
        {
            db.Documents.Add(document);
            db.Chunks.Add(new Chunk
            {
                Id = Guid.CreateVersion7(),
                DocumentId = document.Id,
                DocumentVersion = 1,
                Ordinal = 0,
                Content = $"Belge: İK Yönetmeliği > {Section}\n\nGünlük harcırah 1.500 TL'dir.",
                SectionPath = Section,
                PageNumber = 2,
                SourceType = DocumentSourceType.Pdf,
            });
            await db.SaveChangesAsync();
        }

        await api.Storage.SaveAsync(document.StorageKey, new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF\n")), document.ContentType);

        return document.Id;
    }

    // A question whose one source is that document's chunk; the answer cites it.
    private static async Task<ChatAnswer> AskAboutAsync(ChatTestApi api, TestUser admin, Guid documentId)
    {
        api.Retrieval.Results =
        [
            new SearchResult(Guid.NewGuid(), documentId, "İK Yönetmeliği", Section, 2, "Günlük harcırah 1.500 TL'dir.", 1),
        ];
        api.Model.Answer("Günlük harcırah 1.500 TL'dir [1].");

        return await api.AnswerAsync(admin.Token, "Günlük harcırah ne kadar?");
    }

    private static Task<HttpResponseMessage> DeleteAsync(ChatTestApi api, TestUser admin, Guid documentId)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/documents/{documentId}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin.Token);

        return api.Client.SendAsync(request);
    }

    private static async Task<ConversationResponse> GetConversationAsync(ChatTestApi api, TestUser admin, Guid conversationId)
    {
        using var response = await api.GetAsync(admin.Token, $"/conversations/{conversationId}");
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<ConversationResponse>())!;
    }
}
