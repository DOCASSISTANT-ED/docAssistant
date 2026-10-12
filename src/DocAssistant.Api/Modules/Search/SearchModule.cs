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

        // Scoped: each search uses the request's AppDbContext and tenant.
        services.AddScoped<IKeywordChunkSearch, KeywordChunkSearch>();
        services.AddScoped<IVectorChunkSearch, VectorChunkSearch>();

        // Pure calculation without state.
        services.AddSingleton<IRankFusion, ReciprocalRankFusion>();

        // The entry point for chat and evaluation. No IReranker is registered until a
        // model is chosen; retrieval then returns the fused order (decisions #48).
        services.AddScoped<IChunkRetrieval, ChunkRetrieval>();

        return services;
    }
}
