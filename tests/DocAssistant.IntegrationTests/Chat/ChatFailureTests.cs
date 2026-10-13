using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAssistant.Api.Modules.Chat;
using DocAssistant.Api.Modules.Search;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace DocAssistant.IntegrationTests.Chat;

// Answers that do not reach their end (docs/decisions.md #56): the model fails, writes
// nothing, or the client goes away. Once the stream has started the status is already
// 200, so a failure is reported with an "error" event in place of "done". In every case
// the question stays stored and the answer is not: the history never holds half an answer.
[Collection(DatabaseCollection.Name)]
public class ChatFailureTests(PostgresFixture database)
{
    private const string Question = "Yıllık izin kaç gün?";

    private static readonly SearchResult Leave =
        FakeChunkRetrieval.Source("İK Yönetmeliği", "Yıllık izin 14 gündür.", "Bölüm 2: Yıllık İzin", pageNumber: 1);

    // ---- The model fails ----

    [Fact]
    public async Task AModelThatFailsBeforeWritingEndsTheStreamWithAnErrorEvent()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.FailWith(new HttpRequestException("model is down"));

        using var response = await api.AskAsync(user.Token, Question);
        var answer = await ChatAnswer.ReadAsync(response);

        // The sources were sent before the model was asked; the status could not change any more.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["sources", "error"], answer.EventTypes);
    }

    [Fact]
    public async Task AModelThatFailsWhileWritingEndsTheStreamWithAnErrorEventAfterWhatItWrote()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin ", "14 ", "gündür [1].");
        api.Model.FailWith(new HttpRequestException("model is down"), afterPieces: 2);

        var answer = await Read(await api.AskAsync(user.Token, Question));

        Assert.Equal(["sources", "delta", "delta", "error"], answer.EventTypes);
        Assert.Equal("Yıllık izin 14 ", answer.Text);
    }

    // "done" is the client's sign that the answer is in the history; it must not follow an error.
    [Fact]
    public async Task AFailedAnswerHasNoDoneEvent()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin ", "14 gündür [1].");
        api.Model.FailWith(new InvalidOperationException("boom"), afterPieces: 1);

        var answer = await Read(await api.AskAsync(user.Token, Question));

        Assert.DoesNotContain("done", answer.EventTypes);
        Assert.Equal("error", answer.EventTypes.Last());
    }

    // The error event tells the client that it failed, not why: what went wrong inside
    // (addresses, model names, stack traces) is for the log.
    [Fact]
    public async Task TheErrorEventHasAMessageAndDoesNotSayWhatWentWrongInside()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.FailWith(new HttpRequestException("connection refused: http://internal-model-host:11434 secret-detail"));

        var answer = await Read(await api.AskAsync(user.Token, Question));

        var error = ChatAnswer.Json(answer.Events[^1]);
        Assert.Equal(["message"], error.EnumerateObject().Select(p => p.Name));
        Assert.False(string.IsNullOrWhiteSpace(answer.Error.Message));
        Assert.DoesNotContain(answer.Events, e => e.Data.Contains("internal-model-host", StringComparison.Ordinal));
        Assert.DoesNotContain(answer.Events, e => e.Data.Contains("secret-detail", StringComparison.Ordinal));
        Assert.DoesNotContain(answer.Events, e => e.Data.Contains("HttpRequestException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedAnswerIsNotStoredButItsQuestionIs()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin ", "14 gündür [1].");
        api.Model.FailWith(new HttpRequestException("model is down"), afterPieces: 1);

        await Read(await api.AskAsync(user.Token, Question));

        var message = Assert.Single(await StoredMessagesOfAsync(user));
        Assert.Equal(MessageRole.User, message.Role);
        Assert.Equal(Question, message.Content);
    }

    [Fact]
    public async Task AFailedAnswerInAnExistingConversationLeavesTheEarlierMessagesAsTheyWere()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin 14 gündür [1].");
        var first = await api.AnswerAsync(user.Token, Question);

        api.Model.FailWith(new HttpRequestException("model is down"));
        await Read(await api.AskAsync(user.Token, "Kaç gün önce istemeliyim?", first.Done.ConversationId));

        var messages = await StoredMessagesOfAsync(user);
        Assert.Equal(
            [Question, "Yıllık izin 14 gündür [1].", "Kaç gün önce istemeliyim?"],
            messages.Select(m => m.Content));
        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.User],
            messages.Select(m => m.Role));
    }

    // ---- The model writes nothing ----

    // An empty answer is not an answer: stored, it would be a blank entry in the history.
    [Theory]
    [InlineData]
    [InlineData("")]
    [InlineData("", "")]
    public async Task AModelThatWritesNothingIsAFailure(params string[] pieces)
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer(pieces);

        var answer = await Read(await api.AskAsync(user.Token, Question));

        Assert.Equal(["sources", "error"], answer.EventTypes);
        Assert.Equal(MessageRole.User, Assert.Single(await StoredMessagesOfAsync(user)).Role);
    }

    // ---- After a failure ----

    // The question opened a conversation even though its answer failed. It is in the
    // user's list, holding the question alone, and can be continued.
    [Fact]
    public async Task TheConversationOfAFailedFirstAnswerIsInTheHistoryWithItsQuestion()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.FailWith(new HttpRequestException("model is down"));
        await Read(await api.AskAsync(user.Token, Question));

        using var listResponse = await api.GetAsync(user.Token, "/conversations");
        var listed = Assert.Single((await listResponse.Content.ReadFromJsonAsync<List<ConversationSummary>>())!);
        Assert.Equal(Question, listed.Title);

        using var response = await api.GetAsync(user.Token, $"/conversations/{listed.Id}");
        var conversation = await response.Content.ReadFromJsonAsync<ConversationResponse>();
        var message = Assert.Single(conversation!.Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal(Question, message.Content);
    }

    [Fact]
    public async Task TheSameConversationCanBeAskedAgainAfterAFailure()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin 14 gündür [1].");
        var first = await api.AnswerAsync(user.Token, Question);
        api.Model.FailWith(new HttpRequestException("model is down"));
        await Read(await api.AskAsync(user.Token, "Kaç gün önce istemeliyim?", first.Done.ConversationId));

        api.Model.Answer("En az on gün önce [1].");
        var retried = await api.AnswerAsync(user.Token, "Kaç gün önce istemeliyim?", first.Done.ConversationId);

        Assert.Equal(["sources", "delta", "done"], retried.EventTypes);
        Assert.Equal(first.Done.ConversationId, retried.Done.ConversationId);

        // The unanswered question is part of what the model reads the next time.
        var history = api.Model.Calls[^1].Messages.Skip(1).SkipLast(1).ToList();
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.User],
            history.Select(m => m.Role));
        Assert.Equal("Kaç gün önce istemeliyim?", history[^1].Text);
    }

    // ---- The client goes away ----

    // The reader closes the page or presses stop while the answer is being written. The
    // model is told to stop (it would otherwise keep writing for nobody), and what had
    // been written is not stored.
    [Fact]
    public async Task WhenTheClientDisconnectsTheModelIsStoppedAndTheAnswerIsNotStored()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin ", "14 gündür [1].");

        // The model waits before its second piece until it is told to stop.
        var modelWasStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Model.BeforePiece = async (index, cancellationToken) =>
        {
            if (index != 1)
            {
                return;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                modelWasStopped.TrySetResult();
                throw;
            }
        };

        using var clientGone = new CancellationTokenSource();
        var response = await api.AskAsync(user.Token, new { question = Question }, clientGone.Token);
        var events = ChatAnswer.StreamAsync(response, clientGone.Token).GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("sources", events.Current.Type);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("delta", events.Current.Type);

        await clientGone.CancelAsync();
        response.Dispose();

        await modelWasStopped.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Give the endpoint a moment to finish whatever it would do after the model stopped.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var message = Assert.Single(await StoredMessagesOfAsync(user));
        Assert.Equal(MessageRole.User, message.Role);
        Assert.Equal(Question, message.Content);

        try
        {
            await events.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
            // Reading was cancelled together with the request.
        }
    }

    // ---- Helpers ----

    private static async Task<ChatAnswer> Read(HttpResponseMessage response)
    {
        using (response)
        {
            return await ChatAnswer.ReadAsync(response);
        }
    }

    // Every message of the user's organization, oldest first. The user is new in every
    // test, so these are the messages of this test.
    private async Task<List<Message>> StoredMessagesOfAsync(TestUser user)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == user.TenantId)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync();
    }
}
