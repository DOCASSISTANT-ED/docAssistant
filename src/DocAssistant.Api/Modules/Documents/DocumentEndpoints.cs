using System.Security.Claims;
using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Documents.Storage;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Shared.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

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
            // authenticate with cookies, which a browser sends on its own. Our token is in
            // the Authorization header and has to be added by code, so another site cannot
            // make a logged-in browser call this endpoint. Revisit if the access token ever
            // moves into a cookie (decisions #41).
            .DisableAntiforgery();

        documents.MapGet("/{id:guid}", GetById);

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
