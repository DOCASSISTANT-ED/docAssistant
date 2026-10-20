using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocAssistant.Api.Modules.Identity;

public class EmailVerificationConfiguration : IEntityTypeConfiguration<EmailVerification>
{
    // SHA-256 as hex.
    public const int CodeHashLength = 64;

    public void Configure(EntityTypeBuilder<EmailVerification> builder)
    {
        builder.ToTable("email_verifications");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.CodeHash)
            .IsRequired()
            .HasMaxLength(CodeHashLength)
            .IsFixedLength();

        // One code per account: the database itself refuses a second row.
        builder.HasIndex(v => v.UserId)
            .IsUnique();

        // Cascade: the code means nothing without its account.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(v => v.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
