using DocAssistant.Api.Modules.Ingestion;
using Microsoft.Extensions.AI;
using Pgvector;

namespace DocAssistant.Api.Modules.Embeddings;

// Turns chunk texts into the vectors stored in chunks.embedding. Document processing
// (docs/decisions.md #50) and re-indexing (#54) both go through here, so the same texts
// always give vectors made and checked by the same rules.
public static class ChunkEmbedding
{
    // Texts sent to the embedding model per request: a large document is embedded in
    // several requests instead of one that could run into the HTTP timeout.
    public const int BatchSize = 32;

    // One vector per text, in the same order. model is the configured model's name and is
    // used for the error message only.
    //
    // A model that cannot be reached throws whatever the generator throws; one that
    // answers wrongly throws InvalidOperationException. Callers decide what that means
    // for the document.
    public static async Task<IReadOnlyList<Vector>> EmbedAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string model,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var vectors = new List<Vector>(texts.Count);

        foreach (var batch in texts.Chunk(BatchSize))
        {
            var generated = await generator.GenerateAsync(batch, cancellationToken: cancellationToken);

            if (generated.Count != batch.Length)
            {
                throw new InvalidOperationException(
                    $"The embedding model returned {generated.Count} vectors for {batch.Length} chunks.");
            }

            foreach (var embedding in generated)
            {
                // The column is vector(1024); a model of another size is a configuration
                // mistake and should say so instead of failing inside the database.
                if (embedding.Vector.Length != ChunkConfiguration.EmbeddingDimensions)
                {
                    throw new InvalidOperationException(
                        $"The embedding model '{model}' returned a vector of " +
                        $"{embedding.Vector.Length} dimensions; chunks store {ChunkConfiguration.EmbeddingDimensions}.");
                }

                vectors.Add(new Vector(embedding.Vector));
            }
        }

        return vectors;
    }
}
