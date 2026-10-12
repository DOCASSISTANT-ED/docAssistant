using Microsoft.Extensions.Options;

namespace DocAssistant.Api.Modules.Search;

public static class SearchModule
{
    public static IServiceCollection AddSearchModule(this IServiceCollection services)
    {
        // Candidate counts (decisions #48). The "Search" section is optional; without it
        // the defaults in SearchOptions apply.
        services.AddOptions<SearchOptions>()
            .BindConfiguration(SearchOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Optional too: without a "Reranker" section the reranker is off (decisions #52).
        services.AddOptions<RerankerOptions>()
            .BindConfiguration(RerankerOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Scoped: each search uses the request's AppDbContext and tenant.
        services.AddScoped<IKeywordChunkSearch, KeywordChunkSearch>();
        services.AddScoped<IVectorChunkSearch, VectorChunkSearch>();

        // Pure calculation without state.
        services.AddSingleton<IRankFusion, ReciprocalRankFusion>();

        // The reranker's address and timeout live on its HttpClient. The trailing slash
        // keeps a path in BaseUrl when "rerank" is appended.
        services.AddHttpClient(TeiReranker.HttpClientName, (sp, client) =>
        {
            var reranker = sp.GetRequiredService<IOptions<RerankerOptions>>().Value;

            if (reranker.Enabled)
            {
                client.BaseAddress = new Uri(reranker.BaseUrl!.TrimEnd('/') + "/");
            }

            client.Timeout = TimeSpan.FromSeconds(reranker.TimeoutSeconds);
        });

        // Decided when a search runs, not here, so that the final configuration counts.
        // With the reranker off this gives null: ChunkRetrieval then sees no reranker and
        // returns the fused order (decisions #48, #52).
        services.AddTransient<IReranker>(sp =>
            sp.GetRequiredService<IOptions<RerankerOptions>>().Value.Enabled
                ? new TeiReranker(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(TeiReranker.HttpClientName),
                    sp.GetRequiredService<ILogger<TeiReranker>>())
                : null!);

        // The entry point for chat and evaluation.
        services.AddScoped<IChunkRetrieval, ChunkRetrieval>();

        return services;
    }
}
