using System.Net;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Chat;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Search;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Chat;

// Who may see whose conversation (docs/decisions.md #55, #57). Two walls:
//
// - Between organizations: conversations and messages are tenant data, kept apart by the
//   query filters and by Row-Level Security (#12-15).
// - Between colleagues: a conversation belongs to the user who started it. RLS does not
//   know about users, so this wall is in the application, and only tests guard it.
//
// Somebody else's conversation is answered exactly like one that does not exist: 404,
// so that an id cannot be probed for being in use.
[Collection(DatabaseCollection.Name)]
public class ChatOwnershipTests(PostgresFixture database)
{
    private static readonly SearchResult Leave =
        FakeChunkRetrieval.Source("İK Yönetmeliği", "Yıllık izin 14 gündür.", "Bölüm 2: Yıllık İzin", pageNumber: 1);

    // ---- Every member may ask (#32) ----

    [Fact]
    public async Task AMemberWhoIsNotAnAdminCanAskAndReadTheirOwnConversation()
    {
        await using var api = ChatTestApi.Start(database);
        var admin = await api.RegisterAsync();
        var member = await api.AddMemberAsync(admin.TenantId);
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Yıllık izin 14 gündür [1].");

        var answer = await api.AnswerAsync(member.Token, "Yıllık izin kaç gün?");

        Assert.Equal("Yıllık izin 14 gündür [1].", answer.Text);
        Assert.Equal([answer.Done.ConversationId], await ListedIdsAsync(api, member));
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(api, member, answer.Done.ConversationId));
    }

    // ---- Colleagues: same organization, another user ----

    [Fact]
    public async Task AColleaguesConversationIsNotInTheList()
    {
        await using var api = ChatTestApi.Start(database);
        var owner = await api.RegisterAsync();
        var colleague = await api.AddMemberAsync(owner.TenantId);
        var ofOwner = await api.AnswerAsync(owner.Token, "Maaşım neden kesildi?");
        var ofColleague = await api.AnswerAsync(colleague.Token, "Yıllık izin kaç gün?");

        Assert.Equal([ofOwner.Done.ConversationId], await ListedIdsAsync(api, owner));
        Assert.Equal([ofColleague.Done.ConversationId], await ListedIdsAsync(api, colleague));
    }

    [Fact]
    public async Task AColleaguesConversationCannotBeOpened()
    {
        await using var api = ChatTestApi.Start(database);
        var owner = await api.RegisterAsync();
        var colleague = await api.AddMemberAsync(owner.TenantId);
        var conversation = await api.AnswerAsync(owner.Token, "Maaşım neden kesildi?");

        using var response = await api.GetAsync(colleague.Token, $"/conversations/{conversation.Done.ConversationId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Maaşım", await response.Content.ReadAsStringAsync());
    }

    // The role makes no difference: an admin manages documents, not colleagues' questions.
    [Fact]
    public async Task AnAdminCannotSeeAMembersConversationEither()
    {
        await using var api = ChatTestApi.Start(database);
        var admin = await api.RegisterAsync();
        var member = await api.AddMemberAsync(admin.TenantId);
        var conversation = await api.AnswerAsync(member.Token, "Maaşım neden kesildi?");

        Assert.Empty(await ListedIdsAsync(api, admin));
        Assert.Equal(HttpStatusCode.NotFound, await GetStatusAsync(api, admin, conversation.Done.ConversationId));
    }

    // Asking "into" a colleague's conversation would be the worst leak: the model is
    // given the last messages of the conversation, and would answer from them.
    [Fact]
    public async Task AQuestionCannotBeAddedToAColleaguesConversation()
    {
        await using var api = ChatTestApi.Start(database);
        var owner = await api.RegisterAsync();
        var colleague = await api.AddMemberAsync(owner.TenantId);
        api.Retrieval.Results = [Leave];
        api.Model.Answer("Kesinti nedeni avans ödemesidir.");
        var conversation = await api.AnswerAsync(owner.Token, "Maaşım neden kesildi?");
        var searchesBefore = api.Retrieval.Queries.Count;
        var modelCallsBefore = api.Model.Calls.Count;

        using var response = await api.AskAsync(
            colleague.Token, "Az önce ne konuştuk?", conversation.Done.ConversationId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(searchesBefore, api.Retrieval.Queries.Count);
        Assert.Equal(modelCallsBefore, api.Model.Calls.Count);

        await using var db = database.CreateOwnerDbContext();
        var contents = await db.Messages.IgnoreQueryFilters()
            .Where(m => m.ConversationId == conversation.Done.ConversationId)
            .Select(m => m.Content)
            .ToListAsync();
        Assert.Equal(2, contents.Count);
        Assert.DoesNotContain("Az önce ne konuştuk?", contents);
        Assert.False(await db.Conversations.IgnoreQueryFilters().AnyAsync(c => c.UserId == colleague.UserId));
    }

    // The same answer in both cases, so that an id cannot be tested for being in use.
    [Fact]
    public async Task AColleaguesConversationLooksExactlyLikeOneThatDoesNotExist()
    {
        await using var api = ChatTestApi.Start(database);
        var owner = await api.RegisterAsync();
        var colleague = await api.AddMemberAsync(owner.TenantId);
        var conversation = await api.AnswerAsync(owner.Token, "Maaşım neden kesildi?");
        var existing = conversation.Done.ConversationId;
        var unknown = Guid.NewGuid();

        using var openExisting = await api.GetAsync(colleague.Token, $"/conversations/{existing}");
        using var openUnknown = await api.GetAsync(colleague.Token, $"/conversations/{unknown}");
        using var askExisting = await api.AskAsync(colleague.Token, "Soru", existing);
        using var askUnknown = await api.AskAsync(colleague.Token, "Soru", unknown);

        Assert.Equal(HttpStatusCode.NotFound, openExisting.StatusCode);
        Assert.Equal(openUnknown.StatusCode, openExisting.StatusCode);
        Assert.Equal(await BodyWithoutTraceAsync(openUnknown), await BodyWithoutTraceAsync(openExisting));

        Assert.Equal(HttpStatusCode.NotFound, askExisting.StatusCode);
        Assert.Equal(askUnknown.StatusCode, askExisting.StatusCode);
        Assert.Equal(await BodyWithoutTraceAsync(askUnknown), await BodyWithoutTraceAsync(askExisting));
    }

    // ---- Another organization ----

    [Fact]
    public async Task AnotherOrganizationsConversationIsNotInTheList()
    {
        await using var api = ChatTestApi.Start(database);
        var ofA = await api.RegisterAsync();
        var ofB = await api.RegisterAsync();
        var conversationA = await api.AnswerAsync(ofA.Token, "A firmasının sorusu");
        var conversationB = await api.AnswerAsync(ofB.Token, "B firmasının sorusu");

        Assert.Equal([conversationA.Done.ConversationId], await ListedIdsAsync(api, ofA));
        Assert.Equal([conversationB.Done.ConversationId], await ListedIdsAsync(api, ofB));
    }

    [Fact]
    public async Task AnotherOrganizationsConversationCannotBeOpenedOrAskedInto()
    {
        await using var api = ChatTestApi.Start(database);
        var ofA = await api.RegisterAsync();
        var ofB = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        var conversation = await api.AnswerAsync(ofA.Token, "A firmasının sorusu");
        var modelCallsBefore = api.Model.Calls.Count;

        using var opened = await api.GetAsync(ofB.Token, $"/conversations/{conversation.Done.ConversationId}");
        using var asked = await api.AskAsync(ofB.Token, "Az önce ne konuştuk?", conversation.Done.ConversationId);

        Assert.Equal(HttpStatusCode.NotFound, opened.StatusCode);
        Assert.DoesNotContain("A firmasının", await opened.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, asked.StatusCode);
        Assert.Equal(modelCallsBefore, api.Model.Calls.Count);

        await using var db = database.CreateOwnerDbContext();
        Assert.False(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.TenantId == ofB.TenantId));
    }

    // ---- One user, two organizations ----

    // The same person can be a member of two organizations, with one user id in both.
    // The check "is this my conversation?" passes for them everywhere; what keeps their
    // conversation in A out of B is the organization of the session alone.
    [Fact]
    public async Task AUserOfTwoOrganizationsSeesOnlyTheConversationsOfTheOneTheyAreSignedInto()
    {
        await using var api = ChatTestApi.Start(database);
        var inA = await api.RegisterAsync();
        var otherAdmin = await api.RegisterAsync();
        var inB = await api.AddMemberAsync(otherAdmin.TenantId, MembershipRole.Member, inA.UserId);
        var conversationA = await api.AnswerAsync(inA.Token, "A firmasındaki sorum");
        var conversationB = await api.AnswerAsync(inB.Token, "B firmasındaki sorum");

        Assert.Equal([conversationA.Done.ConversationId], await ListedIdsAsync(api, inA));
        Assert.Equal([conversationB.Done.ConversationId], await ListedIdsAsync(api, inB));
        Assert.Equal(HttpStatusCode.NotFound, await GetStatusAsync(api, inB, conversationA.Done.ConversationId));
        Assert.Equal(HttpStatusCode.NotFound, await GetStatusAsync(api, inA, conversationB.Done.ConversationId));
    }

    [Fact]
    public async Task AUserOfTwoOrganizationsCannotContinueAConversationFromTheOtherOne()
    {
        await using var api = ChatTestApi.Start(database);
        var inA = await api.RegisterAsync();
        var otherAdmin = await api.RegisterAsync();
        var inB = await api.AddMemberAsync(otherAdmin.TenantId, MembershipRole.Member, inA.UserId);
        api.Retrieval.Results = [Leave];
        var conversationA = await api.AnswerAsync(inA.Token, "A firmasındaki sorum");
        var modelCallsBefore = api.Model.Calls.Count;

        using var response = await api.AskAsync(inB.Token, "Devam edelim", conversationA.Done.ConversationId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(modelCallsBefore, api.Model.Calls.Count);

        await using var db = database.CreateOwnerDbContext();
        Assert.False(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.TenantId == otherAdmin.TenantId));
    }

    // ---- The database's own wall ----

    // The tests above go through the API, where the query filters already hide other
    // organizations' rows. Here the filters are switched off and the database is asked
    // as the restricted user the app connects as: Row-Level Security alone must keep the
    // rows of the two chat tables apart (#57, docs/testing.md rule 2).
    [Fact]
    public async Task RowLevelSecurityKeepsConversationsAndMessagesOfOtherOrganizationsOut()
    {
        await using var api = ChatTestApi.Start(database);
        var ofA = await api.RegisterAsync();
        var ofB = await api.RegisterAsync();
        api.Retrieval.Results = [Leave];
        var conversation = await api.AnswerAsync(ofA.Token, "A firmasının sorusu");
        var id = conversation.Done.ConversationId;

        await using (var asA = database.CreateAppDbContext(ofA.TenantId))
        {
            Assert.True(await asA.Conversations.IgnoreQueryFilters().AnyAsync(c => c.Id == id));
            Assert.Equal(2, await asA.Messages.IgnoreQueryFilters().CountAsync(m => m.ConversationId == id));
        }

        await using (var asB = database.CreateAppDbContext(ofB.TenantId))
        {
            Assert.False(await asB.Conversations.IgnoreQueryFilters().AnyAsync(c => c.Id == id));
            Assert.False(await asB.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == id));
        }

        // No organization selected, as before login: nothing at all.
        await using (var asNobody = database.CreateAppDbContext(tenantId: null))
        {
            Assert.False(await asNobody.Conversations.IgnoreQueryFilters().AnyAsync());
            Assert.False(await asNobody.Messages.IgnoreQueryFilters().AnyAsync());
        }
    }

    // ---- Helpers ----

    private static async Task<List<Guid>> ListedIdsAsync(ChatTestApi api, TestUser user)
    {
        using var response = await api.GetAsync(user.Token, "/conversations");
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<List<ConversationSummary>>())!.Select(c => c.Id).ToList();
    }

    private static async Task<HttpStatusCode> GetStatusAsync(ChatTestApi api, TestUser user, Guid conversationId)
    {
        using var response = await api.GetAsync(user.Token, $"/conversations/{conversationId}");

        return response.StatusCode;
    }

    // An error body may carry a trace id, which differs for every request.
    private static async Task<string> BodyWithoutTraceAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var trace = body.IndexOf("\"traceId\"", StringComparison.Ordinal);

        return trace < 0 ? body : body[..trace];
    }
}
