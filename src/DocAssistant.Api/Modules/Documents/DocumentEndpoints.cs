using System.Security.Claims;
using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Documents.Storage;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Ingestion.Chunking;
using DocAssistant.Shared.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace DocAssistant.Api.Modules.Documents;

public static class DocumentEndpoints
{
    // docs/decisions.md #32.
    public const long MaxFileSizeBytes = 20 * 1024 * 1024;

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var documents = app.MapGroup("/documents")
            .RequireAuthorization();

        documents.MapPost("/", Upload)
            // decisions #32: admins upload, members ask questions.
            .RequireAuthorization(policy => policy.RequireRole(nameof(MembershipRole.Admin)))
            // Form endpoints expect antiforgery tokens by default. Those defend sites that
            // authenticate with cookies, which a browser sends on its own. The token does
            // travel in a cookie in the browser (decisions #60), but every cookie-
            // authenticated request that changes something must carry the X-Requested-With
            // header (SessionCookieCsrfProtection). Another site cannot add that header to
            // a form post, so it cannot make a logged-in browser call this endpoint.
            .DisableAntiforgery();

        documents.MapGet("/{id:guid}", GetById);
        documents.MapGet("/{id:guid}/file", GetFile);
        documents.MapGet("/{id:guid}/passages", GetPassages);

        return app;
    }

    // Stores the file, records the document as Pending and queues it for processing.
    // Answers 202 without waiting for processing; GET /documents/{id} reports progress.
    private static async Task<Results<Accepted<DocumentResponse>, ProblemHttpResult>> Upload(
        IFormFile? file,
        ClaimsPrincipal user,
        ITenantContext tenantContext,
        AppDbContext db,
        IFileStorage storage,
        IIngestionQueue queue,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Tenant and uploader come from the validated token only, never from the request.
        if (tenantContext.TenantId is not { } tenantId
            || !Guid.TryParse(user.FindFirstValue(ClaimNames.UserId), out var userId))
        {
            return Problem(StatusCodes.Status403Forbidden, "The access token does not identify a user and an organization.");
        }

        if (file is null || file.Length == 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "A non-empty file is required in the 'file' form field.");
        }

        if (file.Length > MaxFileSizeBytes)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "The file is larger than 20 MB.");
        }

        await using var content = file.OpenReadStream();

        var contentType = await DocumentFileType.DetectAsync(content, cancellationToken);
        if (contentType is null)
        {
            return Problem(StatusCodes.Status415UnsupportedMediaType, "Only PDF and DOCX files can be uploaded.");
        }

        var fileName = DocumentFileName.Clean(file.FileName);
        var documentId = Guid.CreateVersion7();

        var document = new Document
        {
            Id = documentId,
            Title = DocumentFileName.ToTitle(fileName),
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = file.Length,
            StorageKey = DocumentStorageKey.ForOriginal(tenantId, documentId, contentType),
            UploadedByUserId = userId,
        };

        var logger = loggerFactory.CreateLogger(typeof(DocumentEndpoints));

        // decisions #37: file first, then the row, then the queue. A row always has a file
        // behind it; a file whose row could not be written is removed again.
        await storage.SaveAsync(document.StorageKey, content, contentType, cancellationToken);

        try
        {
            // TenantId is stamped by AppDbContext's tenant write rules.
            db.Documents.Add(document);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await DeleteStoredFileAsync(storage, document.StorageKey, logger);
            throw;
        }

        try
        {
            await queue.EnqueueAsync(tenantId, document.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            // The document is safely stored as Pending; startup recovery will queue it
            // (decisions #34, #37). Not worth failing an upload that has succeeded.
            logger.LogWarning(ex, "Document {DocumentId} was stored but could not be queued.", document.Id);
        }

        return TypedResults.Accepted($"/documents/{document.Id}", DocumentResponse.From(document));
    }

    // Any member of the tenant may look. The query filter and RLS limit this to the
    // caller's tenant, so another tenant's document is simply "not found".
    private static async Task<Results<Ok<DocumentResponse>, NotFound>> GetById(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var document = await db.Documents
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

        return document is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(DocumentResponse.From(document));
    }

    // The uploaded file itself, so a source in the chat can be checked against the
    // original (decisions #63). Any member may open it: whoever can ask about a document
    // must be able to see what the answer rests on. As in GetById, the query filter and
    // RLS limit the lookup to the caller's tenant; another tenant's document is "not found".
    private static async Task<Results<FileStreamHttpResult, NotFound>> GetFile(
        Guid id,
        HttpContext context,
        AppDbContext db,
        IFileStorage storage,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var document = await db.Documents
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

        if (document is null)
        {
            return TypedResults.NotFound();
        }

        Stream content;

        try
        {
            content = await storage.OpenReadAsync(document.StorageKey, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // A row always has a file behind it (decisions #37); if it does not, something
            // outside the app removed it. For the user the document is gone either way.
            loggerFactory.CreateLogger(typeof(DocumentEndpoints))
                .LogWarning("Document {DocumentId} has no file under its storage key.", document.Id);

            return TypedResults.NotFound();
        }

        var headers = context.Response.Headers;

        // A PDF opens in the browser's own viewer, where the client can jump to the
        // source's page; a browser cannot show a DOCX, so it is downloaded. The file's
        // own name is sent in both cases, Turkish letters included (filename*).
        var disposition = new ContentDispositionHeaderValue(
            document.ContentType == DocumentContentTypes.Pdf ? "inline" : "attachment");
        disposition.SetHttpFileName(document.FileName);
        headers.ContentDisposition = disposition.ToString();

        // The type is the one detected from the bytes at upload (decisions #32), and the
        // browser must not guess another one: an uploaded file never runs as a page.
        headers.XContentTypeOptions = "nosniff";

        // Company documents may be kept by the user's own browser, never by a shared
        // cache (a proxy on the company's network, for one).
        headers.CacheControl = "private";

        // Range requests let a PDF viewer load the page it shows first.
        return TypedResults.File(content, document.ContentType, enableRangeProcessing: true);
    }

    // The text of a source, for the panel the chat opens beside an answer (decisions #63).
    // A source names its place by section path and page (#56), not by chunk: chunks are
    // replaced when a document is processed again (#35), its sections and pages are not.
    // So the chunks are found by the same two values, and a missing value means "none":
    // a source without a section is text before the first heading (#46), a source
    // without a page is from a DOCX (#22).
    //
    // An empty list is a valid answer: the document exists, but no longer has that place
    // (it was processed again and the section was renamed, for one). Any member may read,
    // and the query filter and RLS keep the lookup inside the caller's tenant.
    private static async Task<Results<Ok<List<PassageResponse>>, NotFound>> GetPassages(
        Guid id,
        string? sectionPath,
        int? pageNumber,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var document = await db.Documents
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

        if (document is null)
        {
            return TypedResults.NotFound();
        }

        // "?sectionPath=" with nothing after it means no section, like leaving it out.
        var section = string.IsNullOrEmpty(sectionPath) ? null : sectionPath;

        var chunks = await db.Chunks
            .AsNoTracking()
            .Where(c => c.DocumentId == id
                && c.DocumentVersion == document.Version
                && c.SectionPath == section
                && c.PageNumber == pageNumber)
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.PageNumber, c.Content })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(chunks
            .Select(c => new PassageResponse(c.PageNumber, DocumentChunker.WithoutContextHeader(c.Content)))
            .ToList());
    }

    private static async Task DeleteStoredFileAsync(IFileStorage storage, string storageKey, ILogger logger)
    {
        try
        {
            // Not the request's token: the cleanup must run even if the client has gone.
            await storage.DeleteAsync(storageKey, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not remove {StorageKey} after its document row failed to save.", storageKey);
        }
    }

    private static ProblemHttpResult Problem(int statusCode, string title) =>
        TypedResults.Problem(statusCode: statusCode, title: title);
}

// What clients see of a document. The storage key and tenant id stay inside the API.
public sealed record DocumentResponse(
    Guid Id,
    string Title,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Status,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt)
{
    public static DocumentResponse From(Document document) => new(
        document.Id,
        document.Title,
        document.FileName,
        document.ContentType,
        document.SizeBytes,
        document.Status.ToString(),
        document.FailureReason,
        document.CreatedAt,
        document.ProcessedAt);
}

// One piece of a source's text, in the document's own words (decisions #63). PageNumber
// is null for a DOCX, which has no pages (decisions #22).
public sealed record PassageResponse(int? PageNumber, string Text);
