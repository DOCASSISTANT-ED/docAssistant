using System.ComponentModel.DataAnnotations;

namespace DocAssistant.Api.Modules.Embeddings;

// Where the embedding model runs and which model it is (docs/decisions.md #8, #50).
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    // The Ollama server, e.g. http://localhost:11434.
    [Required]
    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    // The model's name in Ollama. Also written to chunks.embedding_model, so re-indexing
    // can tell which chunks were embedded with an older model.
    [Required]
    [MaxLength(Ingestion.ChunkConfiguration.EmbeddingModelMaxLength)]
    public string Model { get; init; } = string.Empty;

    // Whether chunks without an embedding of this model are embedded again when the app
    // starts (decisions #54). Off in tests, where every test brings its own model.
    public bool ReindexOnStartup { get; init; } = true;
}
