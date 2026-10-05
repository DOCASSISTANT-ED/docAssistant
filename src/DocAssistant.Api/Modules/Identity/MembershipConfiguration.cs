using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocAssistant.Api.Modules.Identity;

public class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public const int RoleMaxLength = 20;

    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.ToTable("memberships");

        builder.HasKey(m => m.Id);

        // Stored as "Admin" / "Member" so the table is readable without the C# enum.
        builder.Property(m => m.Role)
            .HasConversion<string>()
            .HasMaxLength(RoleMaxLength);

        builder.Property(m => m.CreatedAt)
            .HasDefaultValueSql("now()");

        builder.HasOne(m => m.Tenant)
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // A user can be a member of a tenant only once.
        builder.HasIndex(m => new { m.UserId, m.TenantId })
            .IsUnique();
    }
}
