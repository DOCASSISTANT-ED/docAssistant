using System.Collections.Concurrent;
using DocAssistant.Api.Modules.Documents.Storage;

namespace DocAssistant.IntegrationTests.Documents;

// Stands in for S3 in tests: keeps files in memory so a test can see exactly what was
// stored, and can be told to fail so the upload's error handling can be checked.
internal sealed class InMemoryFileStorage : IFileStorage
{
    private readonly ConcurrentDictionary<string, StoredFile> _files = new();
    private readonly ConcurrentQueue<string> _deletedKeys = new();

    // When true, SaveAsync throws as an unreachable storage server would.
    public bool FailSaves { get; init; }

    public IReadOnlyDictionary<string, StoredFile> Files => _files;

    public IReadOnlyCollection<string> DeletedKeys => _deletedKeys;

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        if (FailSaves)
        {
            throw new IOException("Simulated storage failure.");
        }

        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken);

        _files[key] = new StoredFile(copy.ToArray(), contentType);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
        _files.TryGetValue(key, out var file)
            ? Task.FromResult<Stream>(new MemoryStream(file.Content))
            : throw new FileNotFoundException($"Nothing is stored under {key}.");

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _deletedKeys.Enqueue(key);
        _files.TryRemove(key, out _);

        return Task.CompletedTask;
    }
}

internal sealed record StoredFile(byte[] Content, string ContentType);
