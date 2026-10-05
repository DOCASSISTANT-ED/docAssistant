namespace DocAssistant.Api.Modules.Tenants;

public class Tenant
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
