using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Documents.Storage;
using DocAssistant.Api.Modules.Ingestion.Chunking;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.Api.Modules.Ingestion;

// Reads a document's file, parses it, chunks it and stores the chunks. Status checks, the
// attempt limit and turning DocumentParseException into Failed are the worker's job
// (DocumentIngestionHandler).
public sealed class DocumentProcessor(
    AppDbContext db,
    IFileStorage storage,
    IEnumerable<IDocumentParser> parsers,
    TimeProvider timeProvider) : IDocumentProcessor
{
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

        await SaveChunksAsync(document, sourceType, drafts, cancellationToken);
    }

    // decisions #35: old chunks out, new chunks in and Done, all or nothing. If anything
    // fails before the commit, the transaction is rolled back when it is disposed.
    private async Task SaveChunksAsync(
        Document document,
        DocumentSourceType sourceType,
        IReadOnlyList<ChunkDraft> drafts,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Left behind by an earlier run that was cut off after its commit (decisions #36):
        // a document always has exactly one set of chunks.
        await db.Chunks
            .Where(c => c.DocumentId == document.Id)
            .ExecuteDeleteAsync(cancellationToken);

        db.Chunks.AddRange(drafts.Select(draft => new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = document.Id,
            DocumentVersion = document.Version,
            Ordinal = draft.Ordinal,
            Content = draft.Content,
            PageNumber = draft.PageNumber,
            SectionPath = draft.SectionPath,
            SourceType = sourceType,
        }));

        document.Status = DocumentStatus.Done;
        document.ProcessedAt = timeProvider.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
