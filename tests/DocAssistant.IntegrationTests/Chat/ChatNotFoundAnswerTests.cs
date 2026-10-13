using System.Text.Json;
using DocAssistant.Api.Modules.Chat;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Chat;

// The answer when the documents hold nothing for the question (docs/decisions.md #55,
// phase 4's second exit criterion). When search finds no chunk the model is not called:
// with nothing to read it could only make an answer up. The fixed sentence comes back in
// the same stream shape as any answer.
//
// The sentence is written out here on purpose: the web app shows this exact answer in its
// own way (src/web/src/app/api/chat.ts), so changing it on the server must break a test.
[Collection(DatabaseCollection.Name)]
public class ChatNotFoundAnswerTests(PostgresFixture database)
{
    private const string NotFound = "Bu bilgi belgelerde bulunamadı.";

    private const string Question = "Doğum izni kaç hafta?";

    [Fact]
    public async Task TheAnswerIsTheFixedSentenceWhenSearchFindsNothing()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [];

        var answer = await api.AnswerAsync(user.Token, Question);

        Assert.Equal(NotFound, answer.Text);
    }

    [Fact]
    public async Task TheModelIsNotCalledWhenSearchFindsNothing()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [];
        api.Model.Answer("Doğum izni 16 haftadır.");

        var answer = await api.AnswerAsync(user.Token, Question);

        Assert.Empty(api.Model.Calls);
        Assert.DoesNotContain("16 hafta", answer.Text);
    }

    // "Nothing found" is a result of searching, not a shortcut around it.
    [Fact]
    public async Task TheQuestionIsStillSearchedFor()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [];

        await api.AnswerAsync(user.Token, Question);

        Assert.Equal([Question], api.Retrieval.Queries);
    }

    // The same events as any answer, so the client needs no second way of reading: an
    // empty list of sources, the sentence, done.
    [Fact]
    public async Task TheAnswerHasTheSameStreamShapeAsAnyOther()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [];

        var answer = await api.AnswerAsync(user.Token, Question);

        Assert.Equal(["sources", "delta", "done"], answer.EventTypes);

        var sources = ChatAnswer.Json(answer.Events[0]);
        Assert.Equal(JsonValueKind.Array, sources.ValueKind);
        Assert.Equal(0, sources.GetArrayLength());
    }

    [Fact]
    public async Task TheAnswerIsStoredLikeAnyOther()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [];

        var answer = await api.AnswerAsync(user.Token, Question);

        await using var db = database.CreateOwnerDbContext();
        var messages = await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.ConversationId == answer.Done.ConversationId)
            .ToListAsync();

        var question = Assert.Single(messages, m => m.Role == MessageRole.User);
        Assert.Equal(Question, question.Content);

        var stored = Assert.Single(messages, m => m.Role == MessageRole.Assistant);
        Assert.Equal(answer.Done.MessageId, stored.Id);
        Assert.Equal(NotFound, stored.Content);
        Assert.Empty(stored.Sources);
    }

    // Each question stands on its own search (#55): an earlier answer with sources does
    // not make the model answer a later question that search finds nothing for.
    [Fact]
    public async Task ALaterQuestionWithNothingFoundIsNotAnsweredFromTheConversationSoFar()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [FakeChunkRetrieval.Source("İK Yönetmeliği", "Yıllık izin 14 gündür.", "Bölüm 2")];
        api.Model.Answer("Yıllık izin 14 gündür [1].");
        var first = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        api.Retrieval.Results = [];
        var second = await api.AnswerAsync(user.Token, Question, first.Done.ConversationId);

        Assert.Equal(NotFound, second.Text);
        Assert.Empty(second.Sources);
        Assert.Single(api.Model.Calls);
    }

    // The other way to the same sentence: search found chunks, but the model read them
    // and says the answer is not there. The endpoint passes that on as it is, and the
    // sources the model was given are still listed and stored (#57).
    [Fact]
    public async Task TheSentenceWrittenByTheModelIsPassedOnWithTheSourcesItWasGiven()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [FakeChunkRetrieval.Source("İK Yönetmeliği", "Yıllık izin 14 gündür.", "Bölüm 2")];
        api.Model.Answer("Bu bilgi belgelerde ", "bulunamadı.");

        var answer = await api.AnswerAsync(user.Token, Question);

        Assert.Equal(NotFound, answer.Text);
        Assert.Single(answer.Sources);
        Assert.Single(api.Model.Calls);

        await using var db = database.CreateOwnerDbContext();
        var stored = await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.Id == answer.Done.MessageId);
        Assert.Single(stored.Sources);
    }
}
