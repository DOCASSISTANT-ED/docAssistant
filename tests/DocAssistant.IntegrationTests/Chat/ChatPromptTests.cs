using DocAssistant.Api.Modules.Search;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.Extensions.AI;

namespace DocAssistant.IntegrationTests.Chat;

// What the model is given for a question (docs/decisions.md #55, #61): the instructions,
// the numbered sources, the last messages of the conversation and the question. Read
// from the stand-in model, which keeps every request it receives.
//
// The wording of the instructions is a starting point that will change (#61), so these
// tests are about what must be in the request, not about how it is phrased.
[Collection(DatabaseCollection.Name)]
public class ChatPromptTests(PostgresFixture database)
{
    private static readonly SearchResult Leave =
        FakeChunkRetrieval.Source("İK Yönetmeliği", "Belge: İK Yönetmeliği > Bölüm 2: Yıllık İzin\nYıllık izin 14 gündür.", "Bölüm 2: Yıllık İzin");

    private static readonly SearchResult Allowance =
        FakeChunkRetrieval.Source("İK Yönetmeliği", "Belge: İK Yönetmeliği > Bölüm 3: Harcırah\nGünlük harcırah 1.500 TL'dir.", "Bölüm 3: Harcırah");

    private static readonly SearchResult Shipping =
        FakeChunkRetrieval.Source("Depo Sevkiyat El Kitabı", "Belge: Depo Sevkiyat El Kitabı\nSevkiyatlar sabah yapılır.");

    // ---- Sources and question ----

    [Fact]
    public async Task TheModelIsGivenTheTextOfEverySourceUnderItsNumber()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Allowance, Shipping, Leave];

        var answer = await api.AnswerAsync(user.Token, "Harcırah ne kadar?");

        var request = LastMessage(api);

        // Each number, then that source's whole text, in the order search returned them:
        // the numbers the model writes are the ones the client was sent.
        var positions = new[]
        {
            request.IndexOf("[1]", StringComparison.Ordinal),
            request.IndexOf(Allowance.Content, StringComparison.Ordinal),
            request.IndexOf("[2]", StringComparison.Ordinal),
            request.IndexOf(Shipping.Content, StringComparison.Ordinal),
            request.IndexOf("[3]", StringComparison.Ordinal),
            request.IndexOf(Leave.Content, StringComparison.Ordinal),
        };
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
        Assert.DoesNotContain("[4]", request);

        Assert.Equal(
            [Allowance.DocumentId, Shipping.DocumentId, Leave.DocumentId],
            answer.Sources.OrderBy(s => s.Number).Select(s => s.DocumentId));
    }

    [Fact]
    public async Task TheQuestionComesAfterTheSources()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];

        await api.AnswerAsync(user.Token, "Beş yıllık çalışanın izni kaç gün?");

        var request = LastMessage(api);
        var question = request.IndexOf("Beş yıllık çalışanın izni kaç gün?", StringComparison.Ordinal);
        Assert.True(question > request.IndexOf(Leave.Content, StringComparison.Ordinal));
        Assert.Equal(ChatRole.User, Call(api).Messages[^1].Role);
    }

    // ---- Instructions ----

    [Fact]
    public async Task TheRequestStartsWithInstructionsForTheModel()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];

        await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        var first = Call(api).Messages[0];
        Assert.Equal(ChatRole.System, first.Role);
        Assert.False(string.IsNullOrWhiteSpace(first.Text));
        Assert.Single(Call(api).Messages, m => m.Role == ChatRole.System);
    }

    // The model is told the exact sentence to write when the sources have no answer. The
    // server sends the same sentence itself when search finds nothing, and the web app
    // recognizes it; all three must stay the same sentence.
    [Fact]
    public async Task TheInstructionsNameTheSentenceForAQuestionTheSourcesDoNotAnswer()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];

        await api.AnswerAsync(user.Token, "Doğum izni kaç hafta?");

        Assert.Contains("Bu bilgi belgelerde bulunamadı.", Call(api).Messages[0].Text);
    }

    // The instructions reach the model with the same line breaks on every machine. With
    // Windows line breaks the development model gave worse answers (see ChatPrompt).
    [Fact]
    public async Task TheRequestHasNoWindowsLineBreaks()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];

        await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        Assert.All(Call(api).Messages, m => Assert.DoesNotContain('\r', m.Text));
    }

    // The context size is set in code (#61): Ollama's own default is smaller than a
    // question with eight sources, and what does not fit is cut off without an error.
    [Fact]
    public async Task TheModelIsAskedForTheDecidedContextSize()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];

        await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        var options = Call(api).Options;
        Assert.NotNull(options?.AdditionalProperties);
        Assert.True(options.AdditionalProperties.TryGetValue("num_ctx", out var contextSize));
        Assert.Equal(8192, Convert.ToInt32(contextSize));
    }

    // ---- Search ----

    [Fact]
    public async Task SearchUsesTheLastQuestionOnly()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        var first = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        await api.AnswerAsync(user.Token, "Peki kaç gün önce istemeliyim?", first.Done.ConversationId);

        Assert.Equal(["Yıllık izin kaç gün?", "Peki kaç gün önce istemeliyim?"], api.Retrieval.Queries);
    }

    // ---- The conversation so far ----

    [Fact]
    public async Task TheFirstQuestionIsSentWithoutAnyHistory()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];

        await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        Assert.Equal([ChatRole.System, ChatRole.User], Call(api).Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task ALaterQuestionIsSentWithTheConversationSoFarOldestFirst()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin 14 gündür.");
        var first = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");
        api.Model.Answer("En az on gün önce.");
        await api.AnswerAsync(user.Token, "Kaç gün önce istemeliyim?", first.Done.ConversationId);

        await api.AnswerAsync(user.Token, "Rapor alırsam ne olur?", first.Done.ConversationId);

        var messages = Call(api).Messages;
        Assert.Equal(
            [ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.User],
            messages.Select(m => m.Role));
        Assert.Equal(
            ["Yıllık izin kaç gün?", "Yıllık izin 14 gündür.", "Kaç gün önce istemeliyim?", "En az on gün önce."],
            messages.Skip(1).Take(4).Select(m => m.Text));
        Assert.Contains("Rapor alırsam ne olur?", messages[^1].Text);
    }

    // The last 6 messages (#55): three questions with their answers. Older ones are left
    // out so that a long conversation still fits what the model can read.
    [Fact]
    public async Task OnlyTheLastSixMessagesOfTheConversationAreSent()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];

        Guid? conversationId = null;

        for (var i = 1; i <= 4; i++)
        {
            api.Model.Answer($"cevap-{i}");
            conversationId = (await api.AnswerAsync(user.Token, $"soru-{i}", conversationId)).Done.ConversationId;
        }

        await api.AnswerAsync(user.Token, "soru-5", conversationId);

        var history = Call(api).Messages.Skip(1).SkipLast(1).Select(m => m.Text);
        Assert.Equal(["soru-2", "cevap-2", "soru-3", "cevap-3", "soru-4", "cevap-4"], history);
        Assert.All(Call(api).Messages, m => Assert.DoesNotContain("soru-1", m.Text));
        Assert.All(Call(api).Messages, m => Assert.DoesNotContain("cevap-1", m.Text));
    }

    // The new question appears once, with the sources; it is not also read back from the
    // database as part of the history.
    [Fact]
    public async Task TheNewQuestionIsNotRepeatedInTheHistory()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        var first = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        await api.AnswerAsync(user.Token, "Rapor alırsam ne olur?", first.Done.ConversationId);

        Assert.Single(Call(api).Messages, m => m.Text.Contains("Rapor alırsam ne olur?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheHistoryIsThatOfThisConversationOnly()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("başka-sohbetin-cevabı");
        await api.AnswerAsync(user.Token, "başka-sohbetin-sorusu");
        api.Model.Answer("Yıllık izin 14 gündür.");
        var conversation = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        await api.AnswerAsync(user.Token, "Kaç gün önce istemeliyim?", conversation.Done.ConversationId);

        Assert.All(Call(api).Messages, m => Assert.DoesNotContain("başka-sohbetin", m.Text));
        Assert.Equal(4, Call(api).Messages.Count);
    }

    // An earlier answer's numbers pointed at that question's sources. Next to the new
    // question's sources they would read as references to those, so they are taken out;
    // the rest of the answer stays.
    [Fact]
    public async Task SourceNumbersOfEarlierAnswersAreNotSentAgain()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave, Allowance];
        api.Model.Answer("Yıllık izin 14 gündür [1]. Harcırah 1.500 TL'dir [2].");
        var first = await api.AnswerAsync(user.Token, "İzin ve harcırah ne kadar?");

        await api.AnswerAsync(user.Token, "Kaç gün önce istemeliyim?", first.Done.ConversationId);

        var earlierAnswer = Call(api).Messages[2];
        Assert.Equal(ChatRole.Assistant, earlierAnswer.Role);
        Assert.Equal("Yıllık izin 14 gündür. Harcırah 1.500 TL'dir.", earlierAnswer.Text);
    }

    // Models sometimes put two numbers in one pair of brackets ("[1, 2]"); the evaluation
    // run counts these (#62) and the web app shows them as two badges. They are not taken
    // out of the history yet, so the model reads old numbers next to new sources.
    [Fact(Skip = "Known gap: only single numbers such as [1] are removed from earlier answers; [1, 2] stays. To be fixed by the chat backend's author.")]
    public async Task SourceNumbersSharingOneBracketAreNotSentAgainEither()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave, Allowance];
        api.Model.Answer("İkisi de yönetmelikte yazar [1, 2].");
        var first = await api.AnswerAsync(user.Token, "İzin ve harcırah nerede yazıyor?");

        await api.AnswerAsync(user.Token, "Kaç gün önce istemeliyim?", first.Done.ConversationId);

        Assert.Equal("İkisi de yönetmelikte yazar.", Call(api).Messages[2].Text);
    }

    // What the user saw and what is stored keep their numbers; only the copy sent to the
    // model loses them.
    [Fact]
    public async Task TheStoredAnswerKeepsItsSourceNumbers()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin 14 gündür [1].");
        var first = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");
        await api.AnswerAsync(user.Token, "Kaç gün önce istemeliyim?", first.Done.ConversationId);

        using var response = await api.GetAsync(user.Token, $"/conversations/{first.Done.ConversationId}");

        Assert.Contains("Yıllık izin 14 gündür [1].", await response.Content.ReadAsStringAsync());
    }

    // The most recent request the model received.
    private static ModelCall Call(ChatTestApi api) => api.Model.Calls[^1];

    // Its last message: the sources and the question.
    private static string LastMessage(ChatTestApi api) => Call(api).Messages[^1].Text;
}
