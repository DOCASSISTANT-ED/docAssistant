using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;

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

                // decisions #60: a browser sends the token in the session cookie. The
                // Authorization header comes first and is what other clients use (tests,
                // the evaluation run); with a header present the cookie is not looked at.
                // Either way the token is validated by the same rules above.
                bearer.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (!context.Request.Headers.ContainsKey(HeaderNames.Authorization)
                            && context.Request.Cookies.TryGetValue(SessionCookie.Name, out var token))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },

                    // decisions #67: the organization and the role in the token are taken
                    // at their word only while the membership they name still exists with
                    // that role. Otherwise someone removed from the organization, or an
                    // admin made a member, would keep their old rights until the token
                    // expires (60 minutes, #60).
                    OnTokenValidated = MembershipCheck.ValidateAsync,
                };
            });

        services.AddAuthorization();

        // Scoped: one instance per HTTP request, so one request's tenant can never
        // leak into another request. Registered as itself: ITenantContext is provided by
        // TenantsModule, which uses this reader inside requests (decisions #31).
        services.AddHttpContextAccessor();
        services.AddScoped<JwtTenantContext>();

        // TimeProvider instead of DateTime.UtcNow so tests can control the clock.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<JwtTokenIssuer>();

        // Only the hasher is taken from ASP.NET Core Identity; users and memberships are our
        // own tables. Default: PBKDF2 with HMAC-SHA512, 100,000 iterations, random salt.
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        return services;
    }
}
