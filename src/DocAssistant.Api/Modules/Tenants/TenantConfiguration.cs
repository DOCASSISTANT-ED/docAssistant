using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocAssistant.Api.Modules.Tenants;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public const int NameMaxLength = 200;

    // RFC 1035: a domain name is at most 253 characters.
    public const int DomainMaxLength = 253;

    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name)
            .IsRequired()
            .HasMaxLength(NameMaxLength);

        // decisions #70: the database itself refuses a second company with the same name.
        builder.Property(t => t.NormalizedName)
            .IsRequired()
            .HasMaxLength(NameMaxLength);

        builder.HasIndex(t => t.NormalizedName)
            .IsUnique();

        // A PostgreSQL text array; an empty array is "no email of its own".
        builder.Property(t => t.EmailDomains)
            .HasDefaultValueSql("'{}'::text[]");

        builder.Property(t => t.RegistrationCodeHash)
            .HasMaxLength(64);

        builder.HasIndex(t => t.RegistrationCodeHash)
            .IsUnique();

        builder.Property(t => t.CreatedAt)
            .HasDefaultValueSql("now()");
    }
}
