using System.ComponentModel.DataAnnotations;

namespace DocAssistant.Api.Modules.Documents.Storage;

public sealed class FileStorageOptions
{
    public const string SectionName = "Storage";

    // The S3 endpoint, e.g. http://localhost:8333 for the SeaweedFS container.
    [Required]
    [Url]
    public string ServiceUrl { get; init; } = string.Empty;

    [Required]
    public string AccessKey { get; init; } = string.Empty;

    // Never commit a real one; production reads it from the environment.
    [Required]
    public string SecretKey { get; init; } = string.Empty;

    // Must already exist; the app does not create buckets.
    [Required]
    public string Bucket { get; init; } = string.Empty;
}
