using DocAssistant.Shared.Tenancy;

namespace DocAssistant.Api.Modules.Chat;

// A question or an answer in a conversation (docs/decisions.md #57).
public class Message : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ConversationId { get; set; }

    public MessageRole Role { get; set; }

    public required string Content { get; set; }

    // For an answer: every source the model was given, in the order it was numbered.
    // Stored with the message and not as references to chunks, so the numbers in an old
    // answer keep their meaning after a document is processed again and its chunks are
    // replaced (decisions #35). Empty for a question.
    public List<MessageSource> Sources { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

public enum MessageRole
{
    User,
    Assistant,
}

// The same fields as the "sources" event of the answer stream (decisions #56).
public class MessageSource
{
    // The number the answer text uses for this source, such as 3 for "[3]".
    public int Number { get; set; }

    public Guid DocumentId { get; set; }

    public required string DocumentTitle { get; set; }

    public string? SectionPath { get; set; }

    // Null for DOCX, which has no pages (decisions #22).
    public int? PageNumber { get; set; }
}
