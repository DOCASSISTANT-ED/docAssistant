using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using DocAssistant.Api.Modules.Documents.Storage;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.IntegrationTests.Identity;
using DocAssistant.IntegrationTests.Infrastructure;
using DocAssistant.IntegrationTests.Ingestion;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocAssistant.IntegrationTests.Documents;

// A copy of the API for upload tests (docs/testing.md rule 4): storage is kept in memory
// and the queue only records what it is given, so an uploaded document stays Pending and
// the test can look at everything the endpoint did.
//
// Startup recovery and the worker are switched off. They are not what these tests are
// about, and recovery would otherwise put other tests' unfinished documents in this
// copy's queue.
internal sealed class UploadTestApi : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _api;

    private UploadTestApi(PostgresFixture database, InMemoryFileStorage storage, IIngestionQueue queue)
    {
        Storage = storage;
        Queue = queue;

        _api = database.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IFileStorage>(storage);
            services.AddSingleton(queue);

            var ingestionServices = services
                .Where(service => service.ServiceType == typeof(IHostedService)
                    && (service.ImplementationType == typeof(IngestionRecoveryService)
                        || service.ImplementationType == typeof(IngestionWorker)))
                .ToList();

            foreach (var service in ingestionServices)
            {
                services.Remove(service);
            }
        }));

        Client = _api.CreateClient();
    }

    public HttpClient Client { get; }

    public InMemoryFileStorage Storage { get; }

    public IIngestionQueue Queue { get; }

    // What was queued, when the queue is a RecordingQueue (the default).
    public IReadOnlyList<IngestionWorkItem> QueuedItems => ((RecordingQueue)Queue).Items;

    public IServiceProvider Services => _api.Services;

    public static UploadTestApi Start(
        PostgresFixture database,
        InMemoryFileStorage? storage = null,
        IIngestionQueue? queue = null)
    {
        return new UploadTestApi(database, storage ?? new InMemoryFileStorage(), queue ?? new RecordingQueue());
    }

    // Registers a new organization; its first user is an Admin (decisions #12).
    public async Task<TestAdmin> RegisterAdminAsync()
    {
        var email = TestAccounts.NewEmail();

        var registered = await TestAccounts.RegisterAsync(Client, email);
        registered.EnsureSuccessStatusCode();
        var token = await TestAccounts.ReadTokenAsync(registered);

        var me = await (await TestAccounts.GetCurrentUserAsync(Client, token))
            .Content.ReadFromJsonAsync<CurrentUserResponse>();

        return new TestAdmin(token, Guid.Parse(me!.UserId!), me.TenantId!.Value);
    }

    // A token the API itself would issue, for a user who is a Member of tenantId.
    public string MemberToken(Guid tenantId, Guid? userId = null) =>
        IssueToken(tenantId, userId ?? Guid.NewGuid(), nameof(MembershipRole.Member));

    public string IssueToken(Guid tenantId, Guid userId, string role) =>
        Services.GetRequiredService<JwtTokenIssuer>()
            .Issue(userId, TestAccounts.NewEmail(), tenantId, role)
            .Token;

    // POST /documents with the file in the "file" form field; without a token when token is null.
    public Task<HttpResponseMessage> UploadAsync(string? token, byte[] content, string fileName)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);

        return SendAsync(HttpMethod.Post, "/documents", token, form);
    }

    public Task<HttpResponseMessage> GetDocumentAsync(string? token, Guid documentId) =>
        SendAsync(HttpMethod.Get, $"/documents/{documentId}", token, content: null);

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _api.DisposeAsync();
    }
}

internal sealed record TestAdmin(string Token, Guid UserId, Guid TenantId);

// Smallest files the upload endpoint recognizes. It decides the type from the bytes
// (decisions #32): a PDF starts with "%PDF-", a DOCX is a zip holding word/document.xml.
// The endpoint does not parse them, so they need not be complete documents.
internal static class UploadFiles
{
    public static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.7\n% test file\n%%EOF\n");

    public static byte[] Docx() => Zip("word/document.xml", "<w:document/>");

    // A zip like a spreadsheet: same container as a DOCX, but not a Word document.
    public static byte[] ZipWithoutWordDocument() => Zip("xl/workbook.xml", "<workbook/>");

    public static byte[] Text() => Encoding.UTF8.GetBytes("Bu bir PDF ya da Word belgesi değil.");

    private static byte[] Zip(string entryName, string content)
    {
        using var file = new MemoryStream();

        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(zip.CreateEntry(entryName).Open());
            entry.Write(content);
        }

        return file.ToArray();
    }
}
