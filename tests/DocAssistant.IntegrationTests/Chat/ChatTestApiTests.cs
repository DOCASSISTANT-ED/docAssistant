using DocAssistant.Api.Modules.Chat;
using DocAssistant.IntegrationTests.Infrastructure;

namespace DocAssistant.IntegrationTests.Chat;

// Checks the chat test setup itself: with the stand-in model and search in place, a
// question asked over HTTP comes back as a stream the helpers can read. When this fails,
// the other chat tests fail for the same reason.
[Collection(DatabaseCollection.Name)]
public class ChatTestApiTests(PostgresFixture database)
{
    [Fact]
    public async Task AQuestionIsAnsweredByTheStandInModelFromTheStandInSources()
    {
        await using var api = ChatTestApi.Start(database);
        var user = await api.RegisterAsync();
        api.Retrieval.Results =
        [
            FakeChunkRetrieval.Source("İK Yönetmeliği", "Yıllık izin 14 gündür.", "Bölüm 2: Yıllık İzin", pageNumber: 1),
        ];
        api.Model.Answer("Yıllık izin ", "14 gündür [1].");

        var answer = await api.AnswerAsync(user.Token, "Yıllık izin kaç gün?");

        Assert.Equal(
            [ChatEvents.Sources, ChatEvents.Delta, ChatEvents.Delta, ChatEvents.Done],
            answer.EventTypes);
        Assert.Equal("Yıllık izin 14 gündür [1].", answer.Text);
        Assert.Equal("İK Yönetmeliği", Assert.Single(answer.Sources).DocumentTitle);
        Assert.Equal(["Yıllık izin kaç gün?"], api.Retrieval.Queries);
        Assert.Single(api.Model.Calls);
    }
}
