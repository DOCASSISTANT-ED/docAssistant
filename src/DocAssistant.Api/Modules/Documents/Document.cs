using DocAssistant.Shared.Tenancy;

namespace DocAssistant.Api.Modules.Documents;

public class Document : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Display name; derived from the file name at upload.
    public required string Title { get; set; }

    // The name the user's file had. Never used to build a storage path (decisions #33).
    public required string FileName { get; set; }

    // One of DocumentContentTypes.
    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    // Where the original file lives in object storage.
    public required string StorageKey { get; set; }

    // Stays 1 in phase 2; re-uploading as a new version comes later (decisions #32).
    public int Version { get; set; } = 1;

    public Guid UploadedByUserId { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    // How many times processing has started; the worker gives up after 3 (decisions #38).
    public int AttemptCount { get; set; }

    // Set when Status is Failed; written for the user (decisions #24).
    public string? FailureReason { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
