using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DocAssistant.Api.Modules.Identity;

// Creates the access tokens that JwtBearer later validates. Both sides read the same
// JwtOptions, so issuer, audience and signing key always match.
public sealed class JwtTokenIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    private readonly JsonWebTokenHandler _handler = new();

    // tenantId: null for a platform admin (docs/decisions.md #70), whose token names no
    // organization; the tenant claim is then left out altogether.
    public AccessToken Issue(Guid userId, string email, Guid? tenantId, string role)
    {
        var jwt = options.Value;
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(jwt.AccessTokenMinutes);

        var claims = new Dictionary<string, object>
        {
            [ClaimNames.UserId] = userId.ToString(),
            [ClaimNames.Email] = email,
            [ClaimNames.Role] = role,
        };

        if (tenantId is { } tenant)
        {
            claims[ClaimNames.TenantId] = tenant.ToString();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(jwt.CreateSigningKey(), SecurityAlgorithms.HmacSha256),
        };

        return new AccessToken(_handler.CreateToken(descriptor), expiresAt);
    }
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);
