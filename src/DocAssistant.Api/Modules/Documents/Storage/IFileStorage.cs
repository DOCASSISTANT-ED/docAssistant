namespace DocAssistant.Api.Modules.Documents.Storage;

// Where uploaded files live (docs/decisions.md #33). Callers only know keys; whether the
// bytes sit in SeaweedFS (development) or another S3-compatible store (production) is
// the implementation's business.
//
// Storage has no notion of tenants: isolation comes from the key (see DocumentStorageKey)
// and from the database row that holds it. Never build a key from user input.
public interface IFileStorage
{
    // Stores content under key, replacing anything already there. The stream must be
    // seekable (its length is needed up front); an uploaded form file is. The caller
    // owns the stream and disposes it.
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    // Returns the whole file as a seekable stream positioned at the start; the parsers
    // need to seek. It is held in memory, which the 20 MB upload limit (decisions #32)
    // keeps reasonable. The caller disposes it.
    //
    // Throws FileNotFoundException when nothing is stored under key.
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    // Does nothing when the key does not exist, so cleaning up twice is safe.
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
