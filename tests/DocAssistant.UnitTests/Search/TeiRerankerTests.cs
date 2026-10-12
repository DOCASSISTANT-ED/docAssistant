using System.Net;
using System.Text;
using System.Text.Json;
using DocAssistant.Api.Modules.Search;
using Microsoft.Extensions.Logging;

namespace DocAssistant.UnitTests.Search;

// The reranker client (docs/decisions.md #48, #52): asks the TEI service to score the
// candidates against the question and returns the best in the service's order. The service
// is optional for a search to succeed: whatever goes wrong with it, the candidates come
// back in the order they were given.
//
// No service runs here; a handler stands in for it and answers as each test says.
public class TeiRerankerTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    private static readonly IReadOnlyList<ChunkText> Candidates =
    [
        new(A, "Yıllık izin süresi kıdeme göre belirlenir."),
        new(B, "Harcırah talepleri muhasebe birimine iletilir."),
        new(C, "Forklift hızı depo içinde sınırlıdır."),
    ];

    // ---- A working service ----

    [Fact]
    public async Task CandidatesComeBackInTheServicesOrderWithItsScores()
    {
        var service = FakeService.Answering("""[{"index":0,"score":0.12},{"index":1,"score":0.97},{"index":2,"score":0.55}]""");

        var result = await Reranker(service).RerankAsync("harcırah", Candidates, limit: 3, CancellationToken.None);

        Assert.Equal([new RankedChunk(B, 0.97), new RankedChunk(C, 0.55), new RankedChunk(A, 0.12)], result);
    }

    // The service may answer in any order; the score decides, not the position in the answer.
    [Fact]
    public async Task AnswerSortedByScoreAlreadyIsReadTheSameWay()
    {
        var service = FakeService.Answering("""[{"index":1,"score":0.97},{"index":2,"score":0.55},{"index":0,"score":0.12}]""");

        var result = await Reranker(service).RerankAsync("harcırah", Candidates, limit: 3, CancellationToken.None);

        Assert.Equal([B, C, A], result.Select(r => r.ChunkId));
    }

    [Fact]
    public async Task AtMostLimitCandidatesAreReturned()
    {
        var service = FakeService.Answering("""[{"index":0,"score":0.12},{"index":1,"score":0.97},{"index":2,"score":0.55}]""");

        var result = await Reranker(service).RerankAsync("harcırah", Candidates, limit: 2, CancellationToken.None);

        Assert.Equal([B, C], result.Select(r => r.ChunkId));
    }

    // Retrieval hands the result on by position, so equal scores need a fixed order.
    [Fact]
    public async Task EqualScoresKeepTheOrderTheCandidatesCameIn()
    {
        var service = FakeService.Answering("""[{"index":2,"score":0.5},{"index":1,"score":0.5},{"index":0,"score":0.5}]""");

        var result = await Reranker(service).RerankAsync("soru", Candidates, limit: 3, CancellationToken.None);

        Assert.Equal([A, B, C], result.Select(r => r.ChunkId));
    }

    [Fact]
    public async Task ServiceGetsTheQuestionAndTheTextsInCandidateOrder()
    {
        var service = FakeService.Answering("""[{"index":0,"score":0.1},{"index":1,"score":0.2},{"index":2,"score":0.3}]""");

        await Reranker(service).RerankAsync("Günlük harcırah kaç TL'dir?", Candidates, limit: 3, CancellationToken.None);

        var request = Assert.Single(service.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://reranker.test/rerank", request.Uri);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("Günlük harcırah kaç TL'dir?", body.RootElement.GetProperty("query").GetString());
        Assert.Equal(
            Candidates.Select(candidate => candidate.Content),
            body.RootElement.GetProperty("texts").EnumerateArray().Select(text => text.GetString()));

        // A chunk longer than the model's input must be cut by the service, not rejected.
        Assert.True(body.RootElement.GetProperty("truncate").GetBoolean());
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [InlineData(3, -1)]
    public async Task NothingToRerankGivesNothingWithoutCallingTheService(int candidateCount, int limit)
    {
        var service = FakeService.Answering("[]");

        var result = await Reranker(service).RerankAsync("soru", Candidates.Take(candidateCount).ToList(), limit, CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(service.Requests);
    }

    // ---- A service that cannot be used (decisions #52): the search goes on without it ----

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ErrorAnswerKeepsTheGivenOrder(HttpStatusCode status)
    {
        var service = FakeService.Answering("""{"error":"something went wrong"}""", status);
        var log = new RecordingLogger();

        var result = await Reranker(service, log).RerankAsync("soru", Candidates, limit: 2, CancellationToken.None);

        AssertGivenOrder(result, A, B);
        Assert.Contains(LogLevel.Warning, log.Levels);
    }

    [Fact]
    public async Task UnreachableServiceKeepsTheGivenOrder()
    {
        var service = new FakeService((_, _) => throw new HttpRequestException("Connection refused"));
        var log = new RecordingLogger();

        var result = await Reranker(service, log).RerankAsync("soru", Candidates, limit: 2, CancellationToken.None);

        AssertGivenOrder(result, A, B);
        Assert.Contains(LogLevel.Warning, log.Levels);
    }

    [Theory]
    // Not JSON at all.
    [InlineData("<html>Bad Gateway</html>")]
    [InlineData("")]
    // JSON, but not the list of scores.
    [InlineData("null")]
    [InlineData("""{"index":0,"score":0.5}""")]
    // A score missing, one too many, the same candidate twice, a candidate that was not sent.
    [InlineData("""[{"index":0,"score":0.9},{"index":1,"score":0.8}]""")]
    [InlineData("""[{"index":0,"score":0.9},{"index":1,"score":0.8},{"index":2,"score":0.7},{"index":3,"score":0.6}]""")]
    [InlineData("""[{"index":0,"score":0.9},{"index":0,"score":0.8},{"index":2,"score":0.7}]""")]
    [InlineData("""[{"index":0,"score":0.9},{"index":1,"score":0.8},{"index":7,"score":0.7}]""")]
    [InlineData("""[{"index":0,"score":0.9},{"index":1,"score":0.8},{"index":-1,"score":0.7}]""")]
    public async Task UnusableAnswerKeepsTheGivenOrder(string answer)
    {
        var service = FakeService.Answering(answer);
        var log = new RecordingLogger();

        var result = await Reranker(service, log).RerankAsync("soru", Candidates, limit: 2, CancellationToken.None);

        AssertGivenOrder(result, A, B);
        Assert.Contains(LogLevel.Warning, log.Levels);
    }

    // HttpClient reports its own timeout as a cancellation the caller did not ask for.
    [Fact]
    public async Task ServiceThatTakesTooLongKeepsTheGivenOrder()
    {
        var service = new FakeService(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var log = new RecordingLogger();
        var reranker = Reranker(service, log, timeout: TimeSpan.FromMilliseconds(200));

        var result = await reranker.RerankAsync("soru", Candidates, limit: 3, CancellationToken.None);

        AssertGivenOrder(result, A, B, C);
        Assert.Contains(LogLevel.Warning, log.Levels);
    }

    // The user closed the page: that is not a reranker failure and must not look like a result.
    [Fact]
    public async Task CallersCancellationIsPassedOn()
    {
        using var cancelled = new CancellationTokenSource();
        var service = new FakeService(async (_, cancellationToken) =>
        {
            await cancelled.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Reranker(service).RerankAsync("soru", Candidates, limit: 3, cancelled.Token));
    }

    [Fact]
    public async Task WorkingServiceLogsNoWarning()
    {
        var service = FakeService.Answering("""[{"index":0,"score":0.1},{"index":1,"score":0.2},{"index":2,"score":0.3}]""");
        var log = new RecordingLogger();

        await Reranker(service, log).RerankAsync("soru", Candidates, limit: 3, CancellationToken.None);

        Assert.DoesNotContain(LogLevel.Warning, log.Levels);
    }

    // ---- Helpers ----

    private static TeiReranker Reranker(FakeService service, RecordingLogger? log = null, TimeSpan? timeout = null)
    {
        var http = new HttpClient(service) { BaseAddress = new Uri("http://reranker.test/") };
        if (timeout is { } value)
        {
            http.Timeout = value;
        }

        return new TeiReranker(http, log ?? new RecordingLogger());
    }

    // The first candidates as they were given; no reranker score exists for them.
    private static void AssertGivenOrder(IReadOnlyList<RankedChunk> result, params Guid[] expected)
    {
        Assert.Equal(expected, result.Select(r => r.ChunkId));
        Assert.All(result, r => Assert.Equal(0, r.Score));
    }

    private sealed class FakeService(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<(HttpMethod Method, string? Uri, string Body)> Requests { get; } = [];

        public static FakeService Answering(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri?.ToString(), body));

            return await respond(request, cancellationToken);
        }
    }

    private sealed class RecordingLogger : ILogger<TeiReranker>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }
}
