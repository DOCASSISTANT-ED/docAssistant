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

        // The HNSW index knows nothing about tenants: it finds the nearest vectors of all
        // tenants and the filter then drops the others, which can leave fewer than limit.
        // With iterative scan (pgvector 0.8+) the index keeps going until the filter has
        // let enough rows through; strict_order keeps them exactly in distance order,
        // which fusion relies on. SET LOCAL lasts only for this transaction, so the
        // pooled connection goes back unchanged.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.iterative_scan = 'strict_order'", cancellationToken);

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

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        // Cosine similarity: 1 for the same direction, 0 for unrelated. For reports only (#48).
        return nearest.Select(chunk => new RankedChunk(chunk.Id, 1 - chunk.Distance)).ToList();
    }
}
