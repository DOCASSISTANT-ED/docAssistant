using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocAssistant.Api.Modules.Chat;

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Role)
            .HasConversion<string>()
            .HasMaxLength(10);

        builder.Property(m => m.Content)
            .IsRequired();

        // One jsonb column holding the list (docs/decisions.md #57). No foreign key to
        // documents or chunks: a source must survive its document being processed again.
        builder.OwnsMany(m => m.Sources, sources =>
        {
            sources.ToJson();

            sources.Property(s => s.DocumentTitle)
                .HasMaxLength(DocumentConfiguration.TitleMaxLength);
        });

        builder.Property(m => m.CreatedAt)
            .HasDefaultValueSql("now()");

        // Serves "this conversation's messages, in order".
        builder.HasIndex(m => new { m.ConversationId, m.CreatedAt });

        // Cascade: messages mean nothing without their conversation.
        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, as for conversations.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
