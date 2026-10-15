using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocAssistant.Api.Modules.Identity;

public class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    // SHA-256 as hex.
    public const int TokenHashLength = 64;

    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.ToTable("invitations");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Role)
            .HasConversion<string>()
            .HasMaxLength(MembershipConfiguration.RoleMaxLength);

        builder.Property(i => i.TokenHash)
            .IsRequired()
            .HasMaxLength(TokenHashLength)
            .IsFixedLength();

        // Opening a link looks the invitation up by its key, without a tenant.
        builder.HasIndex(i => i.TokenHash)
            .IsUnique();

        builder.Property(i => i.CreatedAt)
            .HasDefaultValueSql("now()");

        // Cascade, as for memberships: the invitations of a deleted organization are void.
        builder.HasOne(i => i.Tenant)
            .WithMany()
            .HasForeignKey(i => i.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(i => i.CreatedByUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(i => i.AcceptedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
