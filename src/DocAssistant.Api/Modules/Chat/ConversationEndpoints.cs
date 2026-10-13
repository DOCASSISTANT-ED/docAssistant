using System.Security.Claims;
using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.Api.Modules.Chat;

// Reading the chat history (docs/decisions.md #57). A conversation belongs to the user
// who started it: the query filter and RLS keep other tenants out, and every query here
// also asks for the caller's own user id, which keeps colleagues out.
public static class ConversationEndpoints
{
    // The list has no paging yet; this keeps one answer bounded.
    public const int ListLimit = 100;

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var conversations = app.MapGroup("/conversations")
            .RequireAuthorization();

        conversations.MapGet("/", List);
        conversations.MapGet("/{id:guid}", GetById);

        return app;
    }

    // The caller's conversations, newest first.
    private static async Task<Results<Ok<List<ConversationSummary>>, ProblemHttpResult>> List(
        ClaimsPrincipal user,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return NoUser();
        }

        var conversations = await db.Conversations
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(ListLimit)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.CreatedAt))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(conversations);
    }

    // One conversation with its messages in the order they were written. Someone else's
    // conversation is "not found", as in POST /chat (decisions #55).
    private static async Task<Results<Ok<ConversationResponse>, NotFound, ProblemHttpResult>> GetById(
        Guid id,
        ClaimsPrincipal user,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return NoUser();
        }

        var conversation = await db.Conversations
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);

        if (conversation is null)
        {
            return TypedResults.NotFound();
        }

        var messages = await db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == id)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new ConversationResponse(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAt,
            messages.Select(MessageResponse.From).ToList()));
    }

    // The user comes from the validated token only, never from the request.
    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId) =>
        Guid.TryParse(user.FindFirstValue(ClaimNames.UserId), out userId);

    private static ProblemHttpResult NoUser() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "The access token does not identify a user.");
}

public sealed record ConversationSummary(Guid Id, string Title, DateTimeOffset CreatedAt);

public sealed record ConversationResponse(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    IReadOnlyList<MessageResponse> Messages);

// Role is "user" for a question and "assistant" for an answer. Sources has the same
// fields as the "sources" event of the answer stream (decisions #56); empty for a question.
public sealed record MessageResponse(
    Guid Id,
    string Role,
    string Content,
    IReadOnlyList<ChatSource> Sources,
    DateTimeOffset CreatedAt)
{
    public static MessageResponse From(Message message) => new(
        message.Id,
        message.Role == MessageRole.User ? "user" : "assistant",
        message.Content,
        message.Sources
            .Select(s => new ChatSource(s.Number, s.DocumentId, s.DocumentTitle, s.SectionPath, s.PageNumber))
            .ToList(),
        message.CreatedAt);
}
