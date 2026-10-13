using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAssistant.Api.Modules.Chat;
using DocAssistant.Api.Modules.Search;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Chat;

// What a question and its answer leave behind, and how it is read back
// (docs/decisions.md #57): conversations and their messages, GET /conversations and
// GET /conversations/{id}. Who may see whose conversation is in ChatOwnershipTests.
[Collection(DatabaseCollection.Name)]
public class ChatHistoryTests(PostgresFixture database)
{
    private static readonly SearchResult Leave =
        FakeChunkRetrieval.Source("İK Yönetmeliği", "Yıllık izin 14 gündür.", "Bölüm 2: Yıllık İzin", pageNumber: 1);

    private static readonly SearchResult Allowance =
        FakeChunkRetrieval.Source("İK Yönetmeliği", "Günlük harcırah 1.500 TL'dir.", "Bölüm 3: Harcırah", pageNumber: 2);

    private static readonly SearchResult Shipping =
        FakeChunkRetrieval.Source("Depo Sevkiyat El Kitabı", "Sevkiyatlar sabah yapılır.");

    // ---- What is stored ----

    [Fact]
    public async Task TheFirstQuestionOpensAConversationOfTheUser()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        var answer = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        await using var db = database.CreateOwnerDbContext();
        var conversation = await db.Conversations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.UserId == user.UserId);
        Assert.Equal(answer.Done.ConversationId, conversation.Id);
        Assert.Equal(user.TenantId, conversation.TenantId);
        Assert.Equal("Yıllık izin kaç gün?", conversation.Title);
        Assert.InRange(conversation.CreatedAt, before, DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task TheQuestionAndTheAnswerAreStoredInTheConversation()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin ", "14 gündür [1].");

        var answer = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        var messages = await StoredMessagesAsync(answer.Done.ConversationId);
        Assert.Equal([MessageRole.User, MessageRole.Assistant], messages.Select(m => m.Role));
        Assert.Equal("Yıllık izin kaç gün?", messages[0].Content);
        Assert.Empty(messages[0].Sources);
        Assert.Equal("Yıllık izin 14 gündür [1].", messages[1].Content);
        Assert.All(messages, m => Assert.Equal(user.TenantId, m.TenantId));
    }

    // The whole numbered list the model was given, not only the numbers the answer uses:
    // which ones to show is the client's decision (#57).
    [Fact]
    public async Task TheAnswerIsStoredWithEverySourceTheModelWasGiven()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave, Allowance, Shipping];
        api.Model.Answer("Yıllık izin 14 gündür [1].");

        var answer = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        var stored = (await StoredMessagesAsync(answer.Done.ConversationId))[1];
        Assert.Equal([1, 2, 3], stored.Sources.Select(s => s.Number));
        Assert.Equal(
            [Leave.DocumentId, Allowance.DocumentId, Shipping.DocumentId],
            stored.Sources.Select(s => s.DocumentId));

        var second = stored.Sources[1];
        Assert.Equal("İK Yönetmeliği", second.DocumentTitle);
        Assert.Equal("Bölüm 3: Harcırah", second.SectionPath);
        Assert.Equal(2, second.PageNumber);

        var third = stored.Sources[2];
        Assert.Null(third.SectionPath);
        Assert.Null(third.PageNumber);
    }

    [Fact]
    public async Task AQuestionWithTheConversationsIdIsAddedToThatConversation()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin 14 gündür [1].");
        var first = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        api.Model.Answer("En az on gün önce [1].");
        var second = await api.AnswerAsync(user.Token, "Kaç gün önce istemeliyim?", first.Done.ConversationId);

        Assert.Equal(first.Done.ConversationId, second.Done.ConversationId);
        Assert.NotEqual(first.Done.MessageId, second.Done.MessageId);

        var messages = await StoredMessagesAsync(first.Done.ConversationId);
        Assert.Equal(
            ["Yıllık izin kaç gün?", "Yıllık izin 14 gündür [1].", "Kaç gün önce istemeliyim?", "En az on gün önce [1]."],
            messages.Select(m => m.Content));
        Assert.Equal(1, await ConversationCountAsync(user));
    }

    [Fact]
    public async Task AQuestionWithoutAConversationIdOpensANewConversationEveryTime()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();

        var first = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");
        var second = await api.AnswerAsync(user.Token, "Harcırah ne kadar?");

        Assert.NotEqual(first.Done.ConversationId, second.Done.ConversationId);
        Assert.Equal(2, await ConversationCountAsync(user));
    }

    // ---- The title: the first 80 characters of the first question ----

    [Fact]
    public async Task AQuestionOfEightyCharactersIsTheWholeTitle()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        var question = new string('a', 80);

        var answer = await api.AnswerAsync(user.Token, question);

        Assert.Equal(question, await StoredTitleAsync(answer.Done.ConversationId));
    }

    [Fact]
    public async Task ALongerQuestionIsCutToItsFirstEightyCharacters()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        var question = "Şirketimizde beş yılını doldurmuş bir depo çalışanının yıllık ücretli izin süresi kaç gündür ve izin talebi en geç ne zaman yapılmalıdır?";

        var answer = await api.AnswerAsync(user.Token, question);

        var title = await StoredTitleAsync(answer.Done.ConversationId);
        Assert.Equal(question[..80], title);

        // The question itself is stored whole.
        Assert.Equal(question, (await StoredMessagesAsync(answer.Done.ConversationId))[0].Content);
    }

    // An emoji takes two places in a .NET string. Cutting at 80 must not leave half of
    // one: that is not valid text, and the database or the browser may reject it.
    [Fact]
    public async Task TheCutDoesNotLeaveHalfAnEmojiAtTheEndOfTheTitle()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        var question = new string('a', 79) + "😀 devamı";

        var answer = await api.AnswerAsync(user.Token, question);

        var title = await StoredTitleAsync(answer.Done.ConversationId);
        Assert.Equal(new string('a', 79), title);
    }

    [Fact]
    public async Task LaterQuestionsDoNotChangeTheTitle()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        var first = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        await api.AnswerAsync(user.Token, "Harcırah ne kadar?", first.Done.ConversationId);

        Assert.Equal("Yıllık izin kaç gün?", await StoredTitleAsync(first.Done.ConversationId));
    }

    // ---- GET /conversations ----

    [Fact]
    public async Task TheListNeedsASession()
    {
        await using var api = ChatTestApi.Start(database);

        using var response = await api.GetAsync(token: null, "/conversations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheListIsEmptyBeforeTheFirstQuestion()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();

        using var response = await api.GetAsync(user.Token, "/conversations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<ConversationSummary>>())!);
    }

    [Fact]
    public async Task TheListHasTheUsersConversationsNewestFirst()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        var oldest = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");
        var middle = await api.AnswerAsync(user.Token, "Harcırah ne kadar?");
        var newest = await api.AnswerAsync(user.Token, "Mesai saatleri nedir?");

        var list = await ListAsync(api, user);

        Assert.Equal(
            [newest.Done.ConversationId, middle.Done.ConversationId, oldest.Done.ConversationId],
            list.Select(c => c.Id));
        Assert.Equal(["Mesai saatleri nedir?", "Harcırah ne kadar?", "Yıllık izin kaç gün?"], list.Select(c => c.Title));
    }

    // A conversation keeps its place: the list is ordered by when a conversation was
    // opened, and a new question in an old one does not move it up.
    [Fact]
    public async Task ANewQuestionInAnOlderConversationDoesNotAddAnotherEntry()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        var older = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");
        var newer = await api.AnswerAsync(user.Token, "Harcırah ne kadar?");

        await api.AnswerAsync(user.Token, "Kaç gün önce istemeliyim?", older.Done.ConversationId);

        var list = await ListAsync(api, user);
        Assert.Equal([newer.Done.ConversationId, older.Done.ConversationId], list.Select(c => c.Id));
    }

    [Fact]
    public async Task AnEntryOfTheListHasIdTitleAndCreationTime()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        var answer = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        using var response = await api.GetAsync(user.Token, "/conversations");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(["createdAt", "id", "title"], FieldNames(entry));
        Assert.Equal(answer.Done.ConversationId, entry.GetProperty("id").GetGuid());
        Assert.Equal("Yıllık izin kaç gün?", entry.GetProperty("title").GetString());
        Assert.Equal(await StoredCreatedAtAsync(answer.Done.ConversationId), entry.GetProperty("createdAt").GetDateTimeOffset());
    }

    // ---- GET /conversations/{id} ----

    [Fact]
    public async Task AConversationNeedsASession()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        var answer = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        using var response = await api.GetAsync(token: null, $"/conversations/{answer.Done.ConversationId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AConversationThatDoesNotExistIsNotFound()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();

        using var response = await api.GetAsync(user.Token, $"/conversations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AConversationIsReturnedWithItsMessagesInTheOrderTheyWereWritten()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin 14 gündür [1].");
        var first = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");
        api.Model.Answer("En az on gün önce [1].");
        var second = await api.AnswerAsync(user.Token, "Kaç gün önce istemeliyim?", first.Done.ConversationId);

        var conversation = await GetAsync(api, user, first.Done.ConversationId);

        Assert.Equal(first.Done.ConversationId, conversation.Id);
        Assert.Equal("Yıllık izin kaç gün?", conversation.Title);
        Assert.Equal(["user", "assistant", "user", "assistant"], conversation.Messages.Select(m => m.Role));
        Assert.Equal(
            ["Yıllık izin kaç gün?", "Yıllık izin 14 gündür [1].", "Kaç gün önce istemeliyim?", "En az on gün önce [1]."],
            conversation.Messages.Select(m => m.Content));
        Assert.Equal(first.Done.MessageId, conversation.Messages[1].Id);
        Assert.Equal(second.Done.MessageId, conversation.Messages[3].Id);
        Assert.Equal(
            conversation.Messages.Select(m => m.CreatedAt).Order(),
            conversation.Messages.Select(m => m.CreatedAt));
    }

    // Reading an old conversation must give the client what the stream gave it when the
    // answer was written: the same sources under the same numbers.
    [Fact]
    public async Task AnAnswerIsReturnedWithTheSameSourcesTheStreamSent()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave, Allowance, Shipping];
        api.Model.Answer("Harcırah 1.500 TL'dir [2].");
        var answer = await api.AnswerAsync(user.Token, "Harcırah ne kadar?");

        var conversation = await GetAsync(api, user, answer.Done.ConversationId);

        Assert.Empty(conversation.Messages[0].Sources);
        Assert.Equal(answer.Sources, conversation.Messages[1].Sources);
        Assert.Equal(3, conversation.Messages[1].Sources.Count);
    }

    [Fact]
    public async Task AConversationAndItsMessagesHaveTheDecidedFields()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin 14 gündür [1].");
        var answer = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        using var response = await api.GetAsync(user.Token, $"/conversations/{answer.Done.ConversationId}");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["createdAt", "id", "messages", "title"], FieldNames(json.RootElement));

        var message = json.RootElement.GetProperty("messages")[1];
        Assert.Equal(["content", "createdAt", "id", "role", "sources"], FieldNames(message));
        Assert.Equal(
            ["documentId", "documentTitle", "number", "pageNumber", "sectionPath"],
            FieldNames(message.GetProperty("sources")[0]));
    }

    // ---- Helpers ----

    private async Task<List<Message>> StoredMessagesAsync(Guid conversationId)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync();
    }

    private async Task<string> StoredTitleAsync(Guid conversationId)
    {
        await using var db = database.CreateOwnerDbContext();

        return (await db.Conversations.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == conversationId)).Title;
    }

    private async Task<DateTimeOffset> StoredCreatedAtAsync(Guid conversationId)
    {
        await using var db = database.CreateOwnerDbContext();

        return (await db.Conversations.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == conversationId)).CreatedAt;
    }

    private async Task<int> ConversationCountAsync(TestUser user)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Conversations.IgnoreQueryFilters().CountAsync(c => c.UserId == user.UserId);
    }

    private static async Task<List<ConversationSummary>> ListAsync(ChatTestApi api, TestUser user)
    {
        using var response = await api.GetAsync(user.Token, "/conversations");
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<List<ConversationSummary>>())!;
    }

    private static async Task<ConversationResponse> GetAsync(ChatTestApi api, TestUser user, Guid conversationId)
    {
        using var response = await api.GetAsync(user.Token, $"/conversations/{conversationId}");
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<ConversationResponse>())!;
    }

    private static IEnumerable<string> FieldNames(JsonElement json) =>
        json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);
}
