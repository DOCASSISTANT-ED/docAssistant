using System.ComponentModel.DataAnnotations;

namespace DocAssistant.Api.Modules.Search;

// Where the reranker service runs (docs/decisions.md #52). The "Reranker" section is
// optional: without BaseUrl the reranker is off and retrieval keeps the fused order.
public sealed class RerankerOptions
{
    public const string SectionName = "Reranker";

    // The Text Embeddings Inference server, e.g. http://localhost:8088. Which model it
    // serves is set where the server is started (docker-compose.yml), not here.
    [Url]
    public string? BaseUrl { get; init; }

    // How long a search waits for the reranker before going on without it.
    [Range(1, 300)]
    public int TimeoutSeconds { get; init; } = 10;

    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl);
}
