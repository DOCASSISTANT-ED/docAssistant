namespace DocAssistant.Api.Modules.Identity;

// Claim names written into and read from our JWTs. Kept in one place so the token
// creator and the readers can never disagree on spelling.
public static class ClaimNames
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string TenantId = "tenant_id";
    public const string Role = "role";
}
