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

        return services;
    }
}
