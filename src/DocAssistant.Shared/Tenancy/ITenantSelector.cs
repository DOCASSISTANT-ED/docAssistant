namespace DocAssistant.Shared.Tenancy;

/// <summary>
/// Selects the tenant for work that runs outside an HTTP request, such as the ingestion
/// worker. See docs/decisions.md #31.
/// </summary>
/// <remarks>
/// Inside a request the tenant always comes from the caller's token and cannot be chosen
/// in code; that is what keeps one tenant from acting as another.
/// </remarks>
public interface ITenantSelector
{
    /// <summary>
    /// Makes <see cref="ITenantContext.TenantId"/> return <paramref name="tenantId"/> for
    /// the rest of the current dependency-injection scope. Call it before using the
    /// database, and create a new scope for each tenant.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Called inside an HTTP request, or a different tenant is already selected in this scope.
    /// </exception>
    void Select(Guid tenantId);
}
