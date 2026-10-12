using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace DocAssistant.Api.Modules.Embeddings;

public static class EmbeddingsModule
{
    public static IServiceCollection AddEmbeddingsModule(this IServiceCollection services)
    {
        // Checked at startup, like the storage settings: a missing model name should stop
        // the app from starting, not fail the first document.
        services.AddOptions<EmbeddingOptions>()
            .BindConfiguration(EmbeddingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The rest of the app sees only IEmbeddingGenerator (docs/decisions.md #50): turning
        // text into vectors, whichever model or provider does it. Ingestion embeds chunks
        // with it, search embeds the question. Creating the client opens no connection.
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;

            return new OllamaApiClient(new Uri(options.BaseUrl), options.Model);
        });

        // Re-indexing (decisions #54): the service runs once at startup; the reindexer is
        // scoped because it works on one document, with that document's tenant.
        services.AddScoped<ChunkReindexer>();
        services.AddHostedService<ReindexService>();

        return services;
    }
}
