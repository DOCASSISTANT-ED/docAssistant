using System.Collections.Concurrent;
using DocAssistant.Api.Modules.Documents.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DocAssistant.Eval;

// The real API in memory (same Program.cs, endpoints, worker and search), so what is
// measured is what a user gets (docs/decisions.md #48). Two things differ from a
// deployment: uploaded files are kept in memory instead of S3, and the database is the
// run's throwaway one. The embedding model and the chat model are the real ones.
internal sealed class EvalApi(
    RepoPaths paths,
    string connectionString,
    string embeddingUrl,
    string embeddingModel,
    string chatUrl,
    string chatModel,
    string chatProvider,
    string? chatApiKey)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The API reads appsettings.json from its own project folder. A test project gets
        // that folder from a build-time attribute; a console project has to name it.
        builder.UseContentRoot(paths.ApiProject);

        // Not "Development": no user-secrets and no appsettings.Development.json, so a run
        // does not depend on a developer's machine settings.
        builder.UseEnvironment("Evaluation");

        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Jwt:SigningKey", "evaluation-run-signing-key-not-a-secret-0123456789");

        // A run registers its company through open registration (decisions #70).
        builder.UseSetting("Auth:OpenRegistration", "true");

        builder.UseSetting("Embedding:BaseUrl", embeddingUrl);
        builder.UseSetting("Embedding:Model", embeddingModel);

        // Called only when answers are measured (--answers); creating the client opens
        // no connection, so a search-only run does not need the chat model.
        builder.UseSetting("Chat:BaseUrl", chatUrl);
        builder.UseSetting("Chat:Model", chatModel);

        // decisions #68: Ollama, or an OpenAI compatible API whose key comes from the
        // environment (EvalRunner), never from the command line.
        builder.UseSetting("Chat:Provider", chatProvider);
        builder.UseSetting("Chat:ApiKey", chatApiKey);

        // The app refuses to start without e-mail settings (decisions #70); a run sends none.
        builder.UseSetting("Email:Host", "smtp.invalid");
        builder.UseSetting("Email:FromAddress", "evaluation@example.com");

        // The app refuses to start without storage settings; the storage itself is replaced below.
        builder.UseSetting("Storage:ServiceUrl", "http://storage.invalid");
        builder.UseSetting("Storage:AccessKey", "evaluation");
        builder.UseSetting("Storage:SecretKey", "evaluation");
        builder.UseSetting("Storage:Bucket", "evaluation");

        // Keep the report readable: the app's own request and query logs stay out of it.
        builder.UseSetting("Logging:LogLevel:Default", "Warning");

        // The in-memory server has no HTTPS port, which the redirect middleware warns about
        // on the first request; nothing here goes over a network.
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.HttpsPolicy", "Error");

        builder.ConfigureTestServices(services => services.AddSingleton<IFileStorage, InMemoryFileStorage>());
    }

    private sealed class InMemoryFileStorage : IFileStorage
    {
        private readonly ConcurrentDictionary<string, byte[]> _files = new();

        public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            _files[key] = copy.ToArray();
        }

        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            _files.TryGetValue(key, out var file)
                ? Task.FromResult<Stream>(new MemoryStream(file, writable: false))
                : throw new FileNotFoundException($"Nothing is stored under {key}.");

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _files.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }
}
