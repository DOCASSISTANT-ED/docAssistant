using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocAssistant.Api.Modules.Tenants;

// Registering companies (docs/decisions.md #70), for the platform admin only. A company
// gets its email rule and a registration code here; its admins register later with the
// code. The platform admin works without a tenant, so every query here sets the identity
// filters aside on purpose (decisions #15) and reads no company data beyond the company
// itself and how many admins it has.
public static partial class PlatformCompanyEndpoints
{
    // A company with more domains than this is unusual enough to be a mistake.
    public const int MaxEmailDomains = 10;

    public static IEndpointRouteBuilder MapPlatformCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        var companies = app.MapGroup("/platform/companies")
            .RequireAuthorization(policy => policy.RequireRole(PlatformAdmin.Role));

        companies.MapPost("", Create);
        companies.MapGet("", List);
        companies.MapPost("/{id:guid}/registration-code", RenewCode);

        return app;
    }

    // The registration code is in this answer only; the database keeps its hash.
    private static async Task<Results<Created<CompanyCreatedResponse>, ProblemHttpResult>> Create(
        CreateCompanyRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeDomains(request.EmailDomains, out var domains, out var invalid))
        {
            // invalidDomain names it for the screen, which says it in Turkish.
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: $"Not a domain name: {invalid}. Write it like afirma.com, without the part before @.",
                extensions: new Dictionary<string, object?> { ["invalidDomain"] = invalid });
        }

        var name = request.Name.Trim();
        var normalizedName = Tenant.NormalizeName(name);

        if (await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.NormalizedName == normalizedName, cancellationToken))
        {
            return CompanyNameTaken();
        }

        var code = RegistrationCode.New();
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            EmailDomains = domains,
            RegistrationCodeHash = RegistrationCode.Hash(code),
        };

        db.Tenants.Add(tenant);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: AuthEndpoints.CompanyNameIndex })
        {
            // Another request registered the same name between the check and the insert.
            return CompanyNameTaken();
        }

        return TypedResults.Created(
            $"/platform/companies/{tenant.Id}",
            new CompanyCreatedResponse(ToResponse(tenant, adminCount: 0), code));
    }

    // Every company, by name.
    private static async Task<Ok<List<CompanyResponse>>> List(AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Tenants
            .IgnoreQueryFilters()
            .OrderBy(t => t.NormalizedName)
            .Select(t => new
            {
                Tenant = t,
                AdminCount = db.Memberships.Count(m => m.TenantId == t.Id && m.Role == MembershipRole.Admin),
            })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(rows.Select(row => ToResponse(row.Tenant, row.AdminCount)).ToList());
    }

    // A new code; the old one stops working at once (decisions #70). Admins already
    // registered are not affected.
    private static async Task<Results<Ok<RegistrationCodeResponse>, NotFound>> RenewCode(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.IgnoreQueryFilters().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (tenant is null)
        {
            return TypedResults.NotFound();
        }

        var code = RegistrationCode.New();
        tenant.RegistrationCodeHash = RegistrationCode.Hash(code);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new RegistrationCodeResponse(code));
    }

    // Lower case, without a leading "@", each once. Only plain ASCII names are accepted:
    // a domain with Turkish letters is written in its xn-- form.
    public static bool TryNormalizeDomains(IReadOnlyList<string>? input, out List<string> domains, out string? invalid)
    {
        domains = [];
        invalid = null;

        foreach (var raw in input ?? [])
        {
            var domain = raw.Trim().TrimStart('@').ToLowerInvariant();

            if (domain.Length == 0)
            {
                continue;
            }

            if (domain.Length > TenantConfiguration.DomainMaxLength || !DomainName().IsMatch(domain))
            {
                invalid = raw;
                return false;
            }

            if (!domains.Contains(domain))
            {
                domains.Add(domain);
            }
        }

        return true;
    }

    private static CompanyResponse ToResponse(Tenant tenant, int adminCount) =>
        new(tenant.Id, tenant.Name, tenant.EmailDomains, tenant.RegistrationCodeHash is not null, adminCount, tenant.CreatedAt);

    private static ProblemHttpResult CompanyNameTaken() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "A company with this name is already registered.");

    // Labels of letters, digits and inner hyphens, separated by dots, ending in a letters-only
    // top-level label: afirma.com, a-firma.com.tr, xn--rnek-ova.com.
    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+(?:[a-z]{2,63}|xn--[a-z0-9-]{1,59})$")]
    private static partial Regex DomainName();
}

public sealed record CreateCompanyRequest
{
    [Required]
    [MaxLength(TenantConfiguration.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    // Empty or left out: the company has no email of its own (decisions #70).
    [MaxLength(PlatformCompanyEndpoints.MaxEmailDomains)]
    public List<string>? EmailDomains { get; init; }
}

// HasRegistrationCode is false for companies that existed before #70 until a code is made.
public sealed record CompanyResponse(
    Guid Id,
    string Name,
    IReadOnlyList<string> EmailDomains,
    bool HasRegistrationCode,
    int AdminCount,
    DateTimeOffset CreatedAt);

public sealed record CompanyCreatedResponse(CompanyResponse Company, string RegistrationCode);

public sealed record RegistrationCodeResponse(string RegistrationCode);
