using DocAssistant.Shared.Tenancy;

namespace DocAssistant.Api.Modules.Documents;

public class Document : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Title { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; }
}
