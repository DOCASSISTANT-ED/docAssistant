using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocAssistant.Api.Modules.Chat;

public class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    // docs/decisions.md #57: the first 80 characters of the first question.
    public const int TitleMaxLength = 80;

    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversations");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Title)
            .IsRequired()
            .HasMaxLength(TitleMaxLength);

        builder.Property(c => c.CreatedAt)
            .HasDefaultValueSql("now()");

        // Serves "this user's conversations, newest first".
        builder.HasIndex(c => new { c.UserId, c.CreatedAt });

        // Restrict, as for documents: a tenant with data cannot be deleted by accident.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Cascade: a conversation is its user's own and nobody else can read it, so it
        // goes with the user. (Documents stay, because they belong to the tenant.)
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
