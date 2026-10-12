using System.Net;
using System.Text;
using DocAssistant.Api.Modules.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocAssistant.UnitTests.Search;

// How the "Reranker" settings switch the reranker on and off and shape its HTTP client
// (docs/decisions.md #52). Only the search module's own registrations are used.
public class RerankerRegistrationTests
{
    private const string Scores = """[{"index":0,"score":0.5}]""";

    private static readonly IReadOnlyList<ChunkText> OneCandidate = [new(Guid.NewGuid(), "Metin.")];

    [Fact]
    public void WithoutAnAddressThereIsNoReranker()
    {
        using var services = Services([]);

        Assert.Null(services.GetService<IReranker>());
        Assert.False(services.GetRequiredService<IOptions<RerankerOptions>>().Value.Enabled);
    }

    [Fact]
    public void WithAnAddressTheRerankerIsTheTeiClient()
    {
        using var services = Services(new() { ["Reranker:BaseUrl"] = "http://localhost:8088" });

        Assert.IsType<TeiReranker>(services.GetService<IReranker>());
    }

    [Theory]
    [InlineData("http://localhost:8088", "http://localhost:8088/rerank")]
    [InlineData("http://localhost:8088/", "http://localhost:8088/rerank")]
    // A service published under a path (behind a gateway) keeps that path.
    [InlineData("http://gateway.test/tei", "http://gateway.test/tei/rerank")]
    [InlineData("http://gateway.test/tei/", "http://gateway.test/tei/rerank")]
    public async Task RequestGoesToRerankUnderTheConfiguredAddress(string baseUrl, string expectedUri)
    {
        var service = new FakeService(TimeSpan.Zero);
        using var services = Services(new() { ["Reranker:BaseUrl"] = baseUrl }, service);

        await services.GetRequiredService<IReranker>().RerankAsync("soru", OneCandidate, 1, CancellationToken.None);

        Assert.Equal(expectedUri, Assert.Single(service.Uris));
    }

    // A slow reranker must not hold a search for longer than the configured time.
    [Fact]
    public async Task ConfiguredTimeoutEndsTheWaitAndTheGivenOrderIsKept()
    {
        var service = new FakeService(TimeSpan.FromSeconds(30));
        using var services = Services(
            new() { ["Reranker:BaseUrl"] = "http://localhost:8088", ["Reranker:TimeoutSeconds"] = "1" }, service);

        var started = DateTime.UtcNow;
        var result = await services.GetRequiredService<IReranker>().RerankAsync("soru", OneCandidate, 1, CancellationToken.None);

        Assert.Equal(OneCandidate[0].ChunkId, Assert.Single(result).ChunkId);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void TimeoutIsTenSecondsUnlessConfigured()
    {
        using var services = Services(new() { ["Reranker:BaseUrl"] = "http://localhost:8088" });

        Assert.Equal(10, services.GetRequiredService<IOptions<RerankerOptions>>().Value.TimeoutSeconds);
    }

    // A wrong setting should be reported as such, not surface later as a failed search.
    [Theory]
    [InlineData("Reranker:BaseUrl", "not a url")]
    [InlineData("Reranker:TimeoutSeconds", "0")]
    [InlineData("Reranker:TimeoutSeconds", "301")]
    public void InvalidSettingIsRejected(string key, string value)
    {
        using var services = Services(new() { ["Reranker:BaseUrl"] = "http://localhost:8088", [key] = value });

        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<RerankerOptions>>().Value);
    }

    // ---- Known gaps -------------------------------------------------------------------
    // Found while writing these tests. The test states the behaviour that seems right and is
    // skipped because the code does not do it yet; remove Skip when it is fixed (or change
    // the expectation if another behaviour is chosen).

    private const string KnownGap = "Known gap in RerankerOptions; see the pull request that added this test.";

    // RerankerOptions.Enabled treats a blank address as "off", but [Url] rejects it first,
    // and with ValidateOnStart the app then refuses to start. An empty value is how a
    // setting is usually switched off in an environment file (Reranker__BaseUrl=).
    [Theory(Skip = KnownGap)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankAddressCountsAsNoAddress(string baseUrl)
    {
        using var services = Services(new() { ["Reranker:BaseUrl"] = baseUrl });

        Assert.Null(services.GetService<IReranker>());
    }

    private static ServiceProvider Services(Dictionary<string, string?> settings, FakeService? service = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSearchModule();

        if (service is not null)
        {
            services.AddHttpClient(TeiReranker.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => service);
        }

        return services.BuildServiceProvider();
    }

    private sealed class FakeService(TimeSpan delay) : HttpMessageHandler
    {
        public List<string?> Uris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri?.ToString());
            await Task.Delay(delay, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Scores, Encoding.UTF8, "application/json"),
            };
        }
    }
}
