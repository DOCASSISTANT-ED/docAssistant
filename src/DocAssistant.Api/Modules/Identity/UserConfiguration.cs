using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocAssistant.Api.Modules.Identity;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    // RFC 5321 limits an email address to 254 characters.
    public const int EmailMaxLength = 254;

    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Email)
            .IsRequired()
            .HasMaxLength(EmailMaxLength);

        builder.Property(u => u.NormalizedEmail)
            .IsRequired()
            .HasMaxLength(EmailMaxLength);

        // The database itself rejects a second account with the same email (decisions #12).
        builder.HasIndex(u => u.NormalizedEmail)
            .IsUnique();

        builder.Property(u => u.PasswordHash)
            .IsRequired();

        builder.Property(u => u.CreatedAt)
            .HasDefaultValueSql("now()");

        builder.HasMany(u => u.Memberships)
            .WithOne(m => m.User)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
