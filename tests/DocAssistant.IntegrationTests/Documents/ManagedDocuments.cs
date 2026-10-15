using System.Net.Http.Json;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.IntegrationTests.Infrastructure;
using DocAssistant.IntegrationTests.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.Documents;

// Helpers for the document management tests (docs/decisions.md #66): documents in a
// chosen state, with chunks and a stored file, so a test can see exactly what listing
// and deleting do to each of them.
internal static class ManagedDocuments
{
    // Uploads a file through the API. With UploadTestApi the file lands in the in-memory
    // storage, the document stays Pending and nothing processes it.
    public static async Task<DocumentResponse> UploadAsync(
        UploadTestApi api,
        string token,
        string fileName = "ik-yonetmeligi.pdf")
    {
        var response = await api.UploadAsync(token, UploadFiles.Pdf(), fileName);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<DocumentResponse>())!;
    }

    // A document as processing would leave it: uploaded, in the given state, with chunks.
    // Its file is in the API's storage. The state is set directly; how a document gets
    // there is the ingestion tests' business.
    public static async Task<DocumentResponse> PrepareAsync(
        UploadTestApi api,
        PostgresFixture database,
        TestAdmin admin,
        DocumentStatus status,
        int chunkCount = 2,
        string fileName = "ik-yonetmeligi.pdf")
    {
        var document = await UploadAsync(api, admin.Token, fileName);

        await AddChunksAsync(database, admin.TenantId, document.Id, chunkCount);
        await IngestionTestSupport.SetStateAsync(database, document.Id, status);

        return document;
    }

    public static async Task AddChunksAsync(PostgresFixture database, Guid tenantId, Guid documentId, int count)
    {
        await using var db = database.CreateOwnerDbContext(tenantId);

        db.Chunks.AddRange(Enumerable.Range(0, count).Select(ordinal => new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = documentId,
            DocumentVersion = 1,
            Ordinal = ordinal,
            Content = $"Belge: İK Yönetmeliği > Bölüm {ordinal + 1}\n\nParça {ordinal + 1}.",
            SectionPath = $"Bölüm {ordinal + 1}",
            PageNumber = ordinal + 1,
            SourceType = DocumentSourceType.Pdf,
        }));

        await db.SaveChangesAsync();
    }

    // DELETE /documents/{id}; without a token when token is null.
    public static Task<HttpResponseMessage> DeleteAsync(UploadTestApi api, string? token, Guid documentId) =>
        api.SendAsync(HttpMethod.Delete, $"/documents/{documentId}", token, content: null);

    // GET /documents; without a token when token is null.
    public static Task<HttpResponseMessage> ListAsync(UploadTestApi api, string? token) =>
        api.SendAsync(HttpMethod.Get, "/documents", token, content: null);

    public static async Task<List<DocumentResponse>> ListedAsync(UploadTestApi api, string token)
    {
        using var response = await ListAsync(api, token);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<List<DocumentResponse>>())!;
    }

    // ---- The stored truth, read as the superuser with filters off ----

    public static async Task<bool> RowExistsAsync(PostgresFixture database, Guid documentId)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Documents.IgnoreQueryFilters().AnyAsync(d => d.Id == documentId);
    }

    public static async Task<int> ChunkCountAsync(PostgresFixture database, Guid documentId)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Chunks.IgnoreQueryFilters().CountAsync(c => c.DocumentId == documentId);
    }

    public static async Task<string> StorageKeyAsync(PostgresFixture database, Guid documentId)
    {
        await using var db = database.CreateOwnerDbContext();

        return await db.Documents.IgnoreQueryFilters()
            .Where(d => d.Id == documentId)
            .Select(d => d.StorageKey)
            .SingleAsync();
    }
}
