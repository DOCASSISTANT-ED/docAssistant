using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Documents.Storage;
using DocAssistant.Api.Modules.Embeddings;
using DocAssistant.Api.Modules.Ingestion.Chunking;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Pgvector;

namespace DocAssistant.Api.Modules.Ingestion;

// Reads a document's file, parses it, chunks it, embeds the chunks and stores them. Status
// checks, the attempt limit and turning DocumentParseException into Failed are the worker's
// job (DocumentIngestionHandler).
public sealed class DocumentProcessor(
    AppDbContext db,
    IFileStorage storage,
    IEnumerable<IDocumentParser> parsers,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IOptions<EmbeddingOptions> embeddingOptions,
    TimeProvider timeProvider) : IDocumentProcessor
{
    // Chunks sent to the embedding model per request: a large document is embedded in
    // several requests instead of one that could run into the HTTP timeout.
    public const int EmbeddingBatchSize = 32;

    // Shown to the user through documents.failure_reason (decisions #24).
    public const string FileMissingMessage =
        "Belgenin dosyası bulunamadı. Lütfen belgeyi yeniden yükleyin.";

    public const string UnsupportedTypeMessage =
        "Bu dosya türü işlenemiyor. Yalnızca PDF ve DOCX belgeleri desteklenir.";

    public const string NoTextMessage = "Belgede okunabilir metin bulunamadı.";

    public async Task ProcessAsync(Document document, CancellationToken cancellationToken)
    {
        // The upload endpoint only stores PDF and DOCX, so this guards against bad data.
        if (!DocumentSourceTypes.TryFromContentType(document.ContentType, out var sourceType))
        {
            throw new DocumentParseException(UnsupportedTypeMessage);
        }

        var parser = parsers.Single(p => p.SourceType == sourceType);

        Stream content;
        try
        {
            content = await storage.OpenReadAsync(document.StorageKey, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // decisions #42: retrying will not bring the file back.
            throw new DocumentParseException(FileMissingMessage);
        }

        ParsedDocument parsed;
        await using (content)
        {
            parsed = parser.Parse(content);
        }

        var drafts = DocumentChunker.Chunk(document.Title, parsed);

        // Parsers already reject files without text; a document of headings only would
        // still end up here, and "Done with nothing to search" would hide the problem.
        if (drafts.Count == 0)
        {
            throw new DocumentParseException(NoTextMessage);
        }

        // Before the transaction (decisions #50): the model can be slow, and no database
        // connection or lock should be held while waiting for it.
        var embeddings = await EmbedAsync(drafts, cancellationToken);

        await SaveChunksAsync(document, sourceType, drafts, embeddings, cancellationToken);
    }

    // One vector per chunk, in the same order. The embedded text is the chunk's whole
    // content, context header included (decisions #27, #50).
    //
    // A model that cannot be reached or answers wrongly is not the document's fault, so
    // nothing here throws DocumentParseException: the error is passed on, the document
    // stays Processing and is retried up to the attempt limit (decisions #38).
    private async Task<IReadOnlyList<Vector>> EmbedAsync(
        IReadOnlyList<ChunkDraft> drafts,
        CancellationToken cancellationToken)
    {
        var vectors = new List<Vector>(drafts.Count);

        foreach (var batch in drafts.Chunk(EmbeddingBatchSize))
        {
            var generated = await embeddingGenerator.GenerateAsync(
                batch.Select(draft => draft.Content),
                cancellationToken: cancellationToken);

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
                        $"The embedding model '{embeddingOptions.Value.Model}' returned a vector of " +
                        $"{embedding.Vector.Length} dimensions; chunks store {ChunkConfiguration.EmbeddingDimensions}.");
                }

                vectors.Add(new Vector(embedding.Vector));
            }
        }

        return vectors;
    }

    // decisions #35: old chunks out, new chunks in and Done, all or nothing. If anything
    // fails before the commit, the transaction is rolled back when it is disposed.
    private async Task SaveChunksAsync(
        Document document,
        DocumentSourceType sourceType,
        IReadOnlyList<ChunkDraft> drafts,
        IReadOnlyList<Vector> embeddings,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Left behind by an earlier run that was cut off after its commit (decisions #36):
        // a document always has exactly one set of chunks.
        await db.Chunks
            .Where(c => c.DocumentId == document.Id)
            .ExecuteDeleteAsync(cancellationToken);

        db.Chunks.AddRange(drafts.Select((draft, index) => new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = document.Id,
            DocumentVersion = document.Version,
            Ordinal = draft.Ordinal,
            Content = draft.Content,
            PageNumber = draft.PageNumber,
            SectionPath = draft.SectionPath,
            SourceType = sourceType,
            Embedding = embeddings[index],

            // Tells re-indexing which model made the vector (decisions #50).
            EmbeddingModel = embeddingOptions.Value.Model,
        }));

        document.Status = DocumentStatus.Done;
        document.ProcessedAt = timeProvider.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
