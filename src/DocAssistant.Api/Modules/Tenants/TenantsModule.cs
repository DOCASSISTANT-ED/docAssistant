using DocAssistant.Shared.Tenancy;

namespace DocAssistant.Api.Modules.Tenants;

public static class TenantsModule
{
    public static IServiceCollection AddTenantsModule(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        // ITenantContext and ITenantSelector must be the same object within a scope:
        // what Select sets is what the query filters, write rules and RLS interceptor read.
        services.AddScoped<CurrentTenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<CurrentTenantContext>());
        services.AddScoped<ITenantSelector>(sp => sp.GetRequiredService<CurrentTenantContext>());

        return services;
    }
}
