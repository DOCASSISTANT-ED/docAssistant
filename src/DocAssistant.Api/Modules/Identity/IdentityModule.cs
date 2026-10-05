using DocAssistant.Shared.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DocAssistant.Api.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        // Configured from JwtOptions (not read directly from configuration) so validation
        // and token creation can never drift apart.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;

                // Keep claim names as written in the token ("sub", "tenant_id")
                // instead of mapping them to long URI claim types.
                bearer.MapInboundClaims = false;

                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = jwt.CreateSigningKey(),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    NameClaimType = ClaimNames.UserId,
                    RoleClaimType = ClaimNames.Role,
                };
            });

        services.AddAuthorization();

        // Scoped: one instance per HTTP request, so one request's tenant can never
        // leak into another request.
        services.AddHttpContextAccessor();
        services.AddScoped<ITenantContext, JwtTenantContext>();

        // TimeProvider instead of DateTime.UtcNow so tests can control the clock.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<JwtTokenIssuer>();

        return services;
    }
}
