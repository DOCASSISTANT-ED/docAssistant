using DocAssistant.Shared.Tenancy;

namespace DocAssistant.Api.Modules.Chat;

// One chat of one user (docs/decisions.md #57). Colleagues in the same tenant do not see
// each other's conversations: the tenant filter and RLS separate tenants, queries separate
// users with UserId.
public class Conversation : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The user who started the conversation; only they can read or continue it.
    public Guid UserId { get; set; }

    // The start of the first question.
    public required string Title { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
