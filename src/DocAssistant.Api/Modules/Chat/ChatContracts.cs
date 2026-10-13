using System.ComponentModel.DataAnnotations;

namespace DocAssistant.Api.Modules.Chat;

// POST /chat (docs/decisions.md #55).
public sealed record ChatRequest
{
    // Empty to start a new conversation.
    public Guid? ConversationId { get; init; }

    [Required]
    [MaxLength(QuestionMaxLength)]
    public string Question { get; init; } = string.Empty;

    public const int QuestionMaxLength = 2000;
}

// The events of the answer stream and their data (decisions #56).
public static class ChatEvents
{
    // Once, before the answer: the sources the model was given.
    public const string Sources = "sources";

    // As the model writes: the next piece of the answer.
    public const string Delta = "delta";

    // Once, at the end.
    public const string Done = "done";

    // Instead of done, when the answer could not be produced after the stream started.
    public const string Error = "error";
}

// Number is what the answer text uses for this source: 3 for "[3]". The chunk's text is
// not sent; a click on the number opens the document's page.
public sealed record ChatSource(int Number, Guid DocumentId, string DocumentTitle, string? SectionPath, int? PageNumber);

public sealed record ChatDelta(string Text);

public sealed record ChatDone(Guid ConversationId, Guid MessageId);

public sealed record ChatError(string Message);
