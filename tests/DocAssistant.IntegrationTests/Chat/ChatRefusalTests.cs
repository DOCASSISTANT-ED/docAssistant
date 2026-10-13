using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DocAssistant.Api.Modules.Chat;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Chat;

// Requests POST /chat refuses (docs/decisions.md #55). A refusal happens before the
// stream starts, so it is an ordinary HTTP error, and it leaves nothing behind: no
// conversation, no message, no search, no call to the model.
[Collection(DatabaseCollection.Name)]
public class ChatRefusalTests(PostgresFixture database)
{
    private const string Question = "Yıllık izin kaç gün?";

    [Fact]
    public async Task AQuestionWithoutASessionIsRefused()
    {
        await using var api = ChatTestApi.Start(database);

        using var response = await api.AskAsync(token: null, Question);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNothingWasAsked(api);
    }

    [Fact]
    public async Task AQuestionWithATokenTheApiDidNotIssueIsRefused()
    {
        await using var api = ChatTestApi.Start(database);

        using var response = await api.AskAsync("not-a-token", Question);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNothingWasAsked(api);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public async Task AnEmptyQuestionIsRefused(string question)
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();

        using var response = await api.AskAsync(user.Token, question);

        await AssertBadRequestAsync(response);
        AssertNothingWasAsked(api);
        await AssertNothingWasStoredAsync(user);
    }

    [Fact]
    public async Task ARequestWithoutAQuestionIsRefused()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();

        using var response = await api.AskAsync(user.Token, new { });

        await AssertBadRequestAsync(response);
        AssertNothingWasAsked(api);
        await AssertNothingWasStoredAsync(user);
    }

    [Fact]
    public async Task AQuestionOverTheLimitIsRefused()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();

        using var response = await api.AskAsync(user.Token, new string('a', 2001));

        await AssertBadRequestAsync(response);
        AssertNothingWasAsked(api);
        await AssertNothingWasStoredAsync(user);
    }

    // The other side of the limit: 2,000 characters are still a question.
    [Fact]
    public async Task AQuestionAtTheLimitIsAnswered()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        var question = new string('a', 2000);

        var answer = await api.AnswerAsync(user.Token, question);

        Assert.Contains("done", answer.EventTypes);
        Assert.Equal([question], api.Retrieval.Queries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soru")]
    [InlineData("{ \"question\": ")]
    public async Task ABodyThatIsNotTheExpectedJsonIsRefused(string body)
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();

        using var response = await PostRawAsync(api, user.Token, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNothingWasAsked(api);
        await AssertNothingWasStoredAsync(user);
    }

    [Fact]
    public async Task AConversationIdThatIsNotAnIdIsRefused()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();

        using var response = await api.AskAsync(user.Token, new { conversationId = "abc", question = Question });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNothingWasAsked(api);
        await AssertNothingWasStoredAsync(user);
    }

    [Fact]
    public async Task AQuestionForAConversationThatDoesNotExistIsNotFound()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        var unknown = Guid.NewGuid();

        using var response = await api.AskAsync(user.Token, Question, unknown);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        AssertNothingWasAsked(api);
        await AssertNothingWasStoredAsync(user);

        // Not created under the id the client made up, either.
        await using var db = database.CreateOwnerDbContext();
        Assert.False(await db.Conversations.IgnoreQueryFilters().AnyAsync(c => c.Id == unknown));
        Assert.False(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == unknown));
    }

    // A refusal must not use up the conversation: the next, valid question still works.
    [Fact]
    public async Task ARefusedQuestionDoesNotAddToAnExistingConversation()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        var first = await api.AnswerAsync(user.Token, Question);
        var conversationId = first.Done.ConversationId;

        using var refused = await api.AskAsync(user.Token, "", conversationId);

        await AssertBadRequestAsync(refused);

        await using var db = database.CreateOwnerDbContext();
        var messages = await db.Messages.IgnoreQueryFilters()
            .Where(m => m.ConversationId == conversationId)
            .ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.Single(messages, m => m.Role == MessageRole.User);
        Assert.Single(messages, m => m.Role == MessageRole.Assistant);
    }

    // 400 with the problem format every other endpoint uses (RFC 9457), not a stream.
    private static async Task AssertBadRequestAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("event:", body);
    }

    private static void AssertNothingWasAsked(ChatTestApi api)
    {
        Assert.Empty(api.Retrieval.Queries);
        Assert.Empty(api.Model.Calls);
    }

    // The user is new in every test, so any conversation of theirs came from this test.
    private async Task AssertNothingWasStoredAsync(TestUser user)
    {
        await using var db = database.CreateOwnerDbContext();

        Assert.False(await db.Conversations.IgnoreQueryFilters().AnyAsync(c => c.UserId == user.UserId));
        Assert.False(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.TenantId == user.TenantId));
    }

    private static Task<HttpResponseMessage> PostRawAsync(ChatTestApi api, string token, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/chat")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return api.Client.SendAsync(request);
    }
}
