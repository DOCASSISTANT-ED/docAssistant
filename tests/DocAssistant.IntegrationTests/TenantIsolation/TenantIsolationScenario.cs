using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Tenants;
using DocAssistant.IntegrationTests.Documents;
using DocAssistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.IntegrationTests.TenantIsolation;

// Two tenants with their own documents and users, plus Carol, a member of both
// (decisions #12). Written as the superuser and, for documents, with raw SQL, so the
// setup never relies on the query filters, write rules or RLS policies under test.
public sealed class TenantIsolationScenario
{
    public required Guid TenantA { get; init; }
    public required Guid TenantB { get; init; }

    public required Guid DocumentA1 { get; init; }
    public required Guid DocumentA2 { get; init; }
    public required Guid DocumentB1 { get; init; }

    public required Guid Alice { get; init; } // member of A
    public required Guid Bob { get; init; } // member of B
    public required Guid Carol { get; init; } // member of A and B

    public required Guid CarolMembershipA { get; init; }
    public required Guid CarolMembershipB { get; init; }

    public static async Task<TenantIsolationScenario> CreateAsync(PostgresFixture database)
    {
        // Unique per test: the database is shared and never reset (docs/testing.md).
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var scenario = new TenantIsolationScenario
        {
            TenantA = Guid.CreateVersion7(),
            TenantB = Guid.CreateVersion7(),
            DocumentA1 = Guid.CreateVersion7(),
            DocumentA2 = Guid.CreateVersion7(),
            DocumentB1 = Guid.CreateVersion7(),
            Alice = Guid.CreateVersion7(),
            Bob = Guid.CreateVersion7(),
            Carol = Guid.CreateVersion7(),
            CarolMembershipA = Guid.CreateVersion7(),
            CarolMembershipB = Guid.CreateVersion7(),
        };

        await using var db = database.CreateOwnerDbContext();

        db.Tenants.AddRange(
            new Tenant { Id = scenario.TenantA, Name = $"A Lojistik {suffix}" },
            new Tenant { Id = scenario.TenantB, Name = $"B Muhasebe {suffix}" });

        db.Users.AddRange(
            NewUser(scenario.Alice, $"alice-{suffix}@a.test"),
            NewUser(scenario.Bob, $"bob-{suffix}@b.test"),
            NewUser(scenario.Carol, $"carol-{suffix}@accounting.test"));

        db.Memberships.AddRange(
            NewMembership(Guid.CreateVersion7(), scenario.Alice, scenario.TenantA, MembershipRole.Admin),
            NewMembership(Guid.CreateVersion7(), scenario.Bob, scenario.TenantB, MembershipRole.Admin),
            NewMembership(scenario.CarolMembershipA, scenario.Carol, scenario.TenantA, MembershipRole.Member),
            NewMembership(scenario.CarolMembershipB, scenario.Carol, scenario.TenantB, MembershipRole.Member));

        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO documents
                (id, tenant_id, title, status, file_name, content_type, size_bytes, storage_key, uploaded_by_user_id)
            VALUES
                ({scenario.DocumentA1}, {scenario.TenantA}, 'A-1', 'Pending', 'a-1.pdf', {DocumentContentTypes.Pdf}, {TestDocuments.SizeBytes}, {TestDocuments.NewStorageKey()}, {scenario.Alice}),
                ({scenario.DocumentA2}, {scenario.TenantA}, 'A-2', 'Pending', 'a-2.pdf', {DocumentContentTypes.Pdf}, {TestDocuments.SizeBytes}, {TestDocuments.NewStorageKey()}, {scenario.Alice}),
                ({scenario.DocumentB1}, {scenario.TenantB}, 'B-1', 'Pending', 'b-1.pdf', {DocumentContentTypes.Pdf}, {TestDocuments.SizeBytes}, {TestDocuments.NewStorageKey()}, {scenario.Bob})
            """);

        return scenario;
    }

    private static User NewUser(Guid id, string email) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = User.NormalizeEmail(email),
        PasswordHash = "not-used-in-these-tests",
    };

    private static Membership NewMembership(Guid id, Guid userId, Guid tenantId, MembershipRole role) => new()
    {
        Id = id,
        UserId = userId,
        TenantId = tenantId,
        Role = role,
    };
}
