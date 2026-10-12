using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Embeddings;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace DocAssistant.Api.Modules.Search;

// Meaning-based search (docs/decisions.md #50, #51): the question is embedded with the same
// model as the chunks, and the chunks whose vectors are closest to it (cosine distance)
// come back, closest first.
//
// Exact, not approximate: the question is compared with every chunk of the caller's tenant.
// There is no vector index; the tenant's chunks are found through the index on tenant_id.
//
// Written with LINQ, so the query filter limits it to the caller's tenant and RLS applies
// underneath (decisions #14, #15).
public sealed class VectorChunkSearch(
    AppDbContext db,
    ITenantContext tenantContext,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IOptions<EmbeddingOptions> embeddingOptions) : IVectorChunkSearch
{
    public async Task<IReadOnlyList<RankedChunk>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        // Nothing to find: do not spend a call to the model on it.
        if (tenantContext.TenantId is null || string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }

        var model = embeddingOptions.Value.Model;
        var embedding = await embeddingGenerator.GenerateVectorAsync(query, cancellationToken: cancellationToken);

        if (embedding.Length != ChunkConfiguration.EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"The embedding model '{model}' returned a vector of {embedding.Length} dimensions; " +
                $"chunks store {ChunkConfiguration.EmbeddingDimensions}.");
        }

        var question = new Vector(embedding);

        var nearest = await db.Chunks
            // Vectors of another model are not comparable with the question's: until
            // re-indexing has replaced them, such chunks are left out rather than ranked
            // by a meaningless distance. Chunks without an embedding go the same way.
            .Where(chunk => chunk.EmbeddingModel == model && chunk.Embedding != null)
            .Select(chunk => new { chunk.Id, Distance = chunk.Embedding!.CosineDistance(question) })
            .OrderBy(chunk => chunk.Distance)
            // Fusion works on positions, so equal distances must not come back in a random order.
            .ThenBy(chunk => chunk.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        // Cosine similarity: 1 for the same direction, 0 for unrelated. For reports only (#48).
        return nearest.Select(chunk => new RankedChunk(chunk.Id, 1 - chunk.Distance)).ToList();
    }
}
