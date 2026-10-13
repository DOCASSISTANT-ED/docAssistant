using System.Text.Json;
using DocAssistant.Api.Modules.Search;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Chat;

// The shape of the answer stream of POST /chat (docs/decisions.md #56). The web app reads
// this stream by these names, so the event and field names are written out here as the
// decision has them, not taken from the API's own constants.
[Collection(DatabaseCollection.Name)]
public class ChatStreamTests(PostgresFixture database)
{
    private static readonly SearchResult Leave =
        FakeChunkRetrieval.Source("İK Yönetmeliği", "Yıllık izin 14 gündür.", "Bölüm 2: Yıllık İzin", pageNumber: 1);

    private static readonly SearchResult Allowance =
        FakeChunkRetrieval.Source("İK Yönetmeliği", "Günlük harcırah 1.500 TL'dir.", "Bölüm 3: Harcırah", pageNumber: 2);

    private static readonly SearchResult Shipping =
        FakeChunkRetrieval.Source("Depo Sevkiyat El Kitabı", "Sevkiyatlar sabah yapılır.", "Sevkiyat > Saatler");

    [Fact]
    public async Task TheAnswerIsAServerSentEventsStream()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];

        using var response = await api.AskAsync(user.Token, "Yıllık izin kaç gün?");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheSourcesComeFirstThenTheAnswerInPiecesThenDone()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave, Allowance];
        api.Model.Answer("Yıllık izin ", "14 gündür ", "[1].");

        var answer = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        Assert.Equal(["sources", "delta", "delta", "delta", "done"], answer.EventTypes);
    }

    [Fact]
    public async Task EveryPieceTheModelWritesIsSentAsItsOwnDelta()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık ", "izin ", "14 ", "gündür [1].");

        var answer = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        Assert.Equal(["Yıllık ", "izin ", "14 ", "gündür [1]."], answer.Deltas);
        Assert.Equal("Yıllık izin 14 gündür [1].", answer.Text);
    }

    // The point of streaming: the reader sees the first words while the model is still
    // writing the rest. The model is held back before its second piece; the first piece
    // must already have arrived.
    [Fact]
    public async Task APieceIsSentWhileTheModelIsStillWriting()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin ", "14 gündür [1].");

        var secondPieceMayBeWritten = new TaskCompletionSource();
        api.Model.BeforePiece = (index, cancellationToken) =>
            index == 1 ? secondPieceMayBeWritten.Task.WaitAsync(cancellationToken) : Task.CompletedTask;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await api.AskAsync(user.Token, "Yıllık izin kaç gün?");
        await using var events = ChatAnswer.StreamAsync(response, timeout.Token).GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("sources", events.Current.Type);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("delta", events.Current.Type);
        Assert.Equal("Yıllık izin ", ChatAnswer.Json(events.Current).GetProperty("text").GetString());

        secondPieceMayBeWritten.SetResult();

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("delta", events.Current.Type);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("done", events.Current.Type);
        Assert.False(await events.MoveNextAsync());
    }

    [Fact]
    public async Task TheSourcesAreNumberedFromOneInTheOrderSearchReturnedThem()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Allowance, Shipping, Leave];

        var answer = await api.AnswerAsync(user.Token, "Harcırah ne kadar?");

        Assert.Equal([1, 2, 3], answer.Sources.Select(s => s.Number));
        Assert.Equal(
            [Allowance.DocumentId, Shipping.DocumentId, Leave.DocumentId],
            answer.Sources.Select(s => s.DocumentId));
    }

    [Fact]
    public async Task ASourceSaysWhichDocumentSectionAndPageItIs()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Allowance];

        var answer = await api.AnswerAsync(user.Token, "Harcırah ne kadar?");

        var source = Assert.Single(answer.Sources);
        Assert.Equal(Allowance.DocumentId, source.DocumentId);
        Assert.Equal("İK Yönetmeliği", source.DocumentTitle);
        Assert.Equal("Bölüm 3: Harcırah", source.SectionPath);
        Assert.Equal(2, source.PageNumber);
    }

    // A DOCX has no pages, and text before the first heading has no section (#22, #46).
    [Fact]
    public async Task ASourceWithoutPageOrSectionIsSentWithThoseFieldsEmpty()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [FakeChunkRetrieval.Source("Uzaktan Çalışma Politikası", "Giriş metni.")];

        var answer = await api.AnswerAsync(user.Token, "Uzaktan çalışma var mı?");

        var source = ChatAnswer.Json(answer.Events[0])[0];
        Assert.Equal(JsonValueKind.Null, source.GetProperty("sectionPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, source.GetProperty("pageNumber").ValueKind);
    }

    // The decision lists the fields; the web app's types (src/web/src/app/api/chat.ts)
    // are written by hand from the same list (#59).
    [Fact]
    public async Task TheEventsCarryExactlyTheDecidedFields()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("14 gündür [1].");

        var answer = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        var sources = ChatAnswer.Json(answer.Events[0]);
        Assert.Equal(JsonValueKind.Array, sources.ValueKind);
        Assert.Equal(
            ["documentId", "documentTitle", "number", "pageNumber", "sectionPath"],
            FieldNames(sources[0]));
        Assert.Equal(["text"], FieldNames(ChatAnswer.Json(answer.Events[1])));
        Assert.Equal(["conversationId", "messageId"], FieldNames(ChatAnswer.Json(answer.Events[2])));
    }

    // The click on a number opens the document; the text the model read stays on the server.
    [Fact]
    public async Task TheTextOfASourceIsNotSent()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        // Plain letters only: JSON may write Turkish letters as \u escapes, which a search
        // of the raw data would miss.
        api.Retrieval.Results = [FakeChunkRetrieval.Source("İK Yönetmeliği", "source-text-7f3a", "Bölüm 2", pageNumber: 1)];
        api.Model.Answer("Cevap [1].");

        var answer = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        Assert.DoesNotContain(answer.Events, e => e.Data.Contains("source-text-7f3a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TurkishLettersArriveUnchanged()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results =
        [
            FakeChunkRetrieval.Source("Çalışan Öğle Yemeği Yönergesi", "Metin.", "Bölüm 1: Ücret ve Ödeme Günü"),
        ];
        api.Model.Answer("Ödeme ayın ", "üçüncü iş günü ", "yapılır; ğ, ü, ş, ı, ö, ç, İ [1].");

        var answer = await api.AnswerAsync(user.Token, "Öğle yemeği ücreti ne zaman ödenir?");

        Assert.Equal("Ödeme ayın üçüncü iş günü yapılır; ğ, ü, ş, ı, ö, ç, İ [1].", answer.Text);
        var source = Assert.Single(answer.Sources);
        Assert.Equal("Çalışan Öğle Yemeği Yönergesi", source.DocumentTitle);
        Assert.Equal("Bölüm 1: Ücret ve Ödeme Günü", source.SectionPath);
    }

    [Fact]
    public async Task DoneNamesTheConversationAndTheStoredAnswer()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin ", "14 gündür [1].");

        var answer = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        await using var db = database.CreateOwnerDbContext();
        var stored = await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.Id == answer.Done.MessageId);
        Assert.Equal(answer.Done.ConversationId, stored.ConversationId);
        Assert.Equal("Yıllık izin 14 gündür [1].", stored.Content);

        var conversation = await db.Conversations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Id == answer.Done.ConversationId);
        Assert.Equal(user.UserId, conversation.UserId);
    }

    private static IEnumerable<string> FieldNames(JsonElement json) =>
        json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);
}
