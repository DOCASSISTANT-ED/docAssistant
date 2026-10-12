using DocAssistant.Api.Data;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DocAssistant.Api.Modules.Embeddings;

// Re-embeds one document's outdated chunks: those without an embedding or with one made
// by another model than the configured one (docs/decisions.md #54). The chunks' text is
// not touched; only embedding and embedding_model are written.
//
// Scoped: ReindexService resolves it from a new scope per document, because a scope can
// hold only one tenant (decisions #31).
public sealed class ChunkReindexer(
    AppDbContext db,
    ITenantSelector tenantSelector,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IOptions<EmbeddingOptions> embeddingOptions)
{
    // Returns how many chunks were re-embedded.
    public async Task<int> ReindexAsync(ReindexItem item, CancellationToken cancellationToken)
    {
        // Before any database access: query filters, the write check and RLS all read
        // the tenant from this scope.
        tenantSelector.Select(item.TenantId);

        var model = embeddingOptions.Value.Model;

        var outdated = await db.Chunks
            .Where(chunk => chunk.DocumentId == item.DocumentId
                && (chunk.Embedding == null || chunk.EmbeddingModel != model))
            .OrderBy(chunk => chunk.Ordinal)
            .ToListAsync(cancellationToken);

        if (outdated.Count == 0)
        {
            return 0;
        }

        // The whole content, context header included, as when the chunk was first
        // embedded (decisions #50). No transaction is open yet: the model can be slow.
        var vectors = await ChunkEmbedding.EmbedAsync(
            embeddingGenerator,
            model,
            outdated.Select(chunk => chunk.Content).ToList(),
            cancellationToken);

        for (var i = 0; i < outdated.Count; i++)
        {
            outdated[i].Embedding = vectors[i];
            outdated[i].EmbeddingModel = model;
        }

        // One SaveChanges is one transaction: all of the document's chunks or none.
        await db.SaveChangesAsync(cancellationToken);

        return outdated.Count;
    }
}

// One "re-embed this document's chunks" job, as documents_to_reindex() returns it.
public sealed record ReindexItem(Guid TenantId, Guid DocumentId);
