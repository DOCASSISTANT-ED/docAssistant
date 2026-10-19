using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Search;
using DocAssistant.Shared.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace DocAssistant.Api.Modules.Chat;

public static class ChatEndpoints
{
    // docs/decisions.md #55: the model also gets the last messages of the conversation.
    public const int HistoryMessages = 6;

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        // decisions #32: every signed-in member may ask.
        app.MapPost("/chat", Ask)
            .RequireAuthorization();

        return app;
    }

    // Everything that can refuse the request happens here, before the stream starts, so
    // the refusal is an ordinary HTTP error. Once the first event is written the status
    // is 200 and cannot change any more; later failures are reported with an error event.
    private static async Task<Results<ServerSentEventsResult<object>, NotFound, ProblemHttpResult>> Ask(
        ChatRequest request,
        ClaimsPrincipal user,
        ITenantContext tenantContext,
        AppDbContext db,
        IChunkRetrieval retrieval,
        IChatClient chatClient,
        IOptions<ChatModelOptions> modelOptions,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Tenant and user come from the validated token only, never from the request.
        if (tenantContext.TenantId is null
            || !Guid.TryParse(user.FindFirstValue(ClaimNames.UserId), out var userId))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "The access token does not identify a user and an organization.");
        }

        var question = request.Question.Trim();

        Conversation? conversation;
        IReadOnlyList<Message> history = [];

        if (request.ConversationId is { } conversationId)
        {
            // Someone else's conversation is treated like one that does not exist
            // (decisions #55): the answer must not reveal that the id is in use.
            conversation = await db.Conversations
                .SingleOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, cancellationToken);

            if (conversation is null)
            {
                return TypedResults.NotFound();
            }

            // The newest messages, then turned back into the order they were written in.
            history = (await db.Messages
                    .AsNoTracking()
                    .Where(m => m.ConversationId == conversationId)
                    .OrderByDescending(m => m.CreatedAt)
                    .ThenByDescending(m => m.Id)
                    .Take(HistoryMessages)
                    .ToListAsync(cancellationToken))
                .AsEnumerable()
                .Reverse()
                .ToList();
        }
        else
        {
            conversation = new Conversation
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                Title = TitleFrom(question),
                CreatedAt = timeProvider.GetUtcNow(),
            };

            db.Conversations.Add(conversation);
        }

        // The question is stored before the answer is attempted and stays even when the
        // answer fails (decisions #56).
        db.Messages.Add(new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversation.Id,
            Role = MessageRole.User,
            Content = question,
            CreatedAt = timeProvider.GetUtcNow(),
        });

        await db.SaveChangesAsync(cancellationToken);

        // Search uses the last question only (decisions #55).
        var sources = await retrieval.RetrieveAsync(question, cancellationToken);

        var answer = new Answer(
            conversation.Id,
            sources,
            ChatPrompt.Build(sources, history, question),
            ModelOptions(modelOptions.Value));

        return TypedResults.ServerSentEvents(
            StreamAsync(answer, db, chatClient, timeProvider, loggerFactory.CreateLogger(typeof(ChatEndpoints)), cancellationToken));
    }

    // The answer stream (decisions #56): sources, then delta as the model writes, then
    // done. The method produces events one at a time (IAsyncEnumerable), and each one is
    // sent to the client as soon as it is produced.
    //
    // When the client disconnects, cancellationToken is cancelled: the model call stops
    // and nothing more runs here, so an answer cut short is never stored.
    private static async IAsyncEnumerable<SseItem<object>> StreamAsync(
        Answer answer,
        AppDbContext db,
        IChatClient chatClient,
        TimeProvider timeProvider,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var numbered = answer.Sources
            .Select((source, index) => new ChatSource(
                index + 1, source.DocumentId, source.DocumentTitle, source.SectionPath, source.PageNumber))
            .ToList();

        // First, so the client can make a number clickable the moment it appears.
        yield return new SseItem<object>(numbered, ChatEvents.Sources);

        var text = new StringBuilder();
        var failed = false;

        if (numbered.Count == 0)
        {
            // Nothing to base an answer on: the model is not called, so it cannot make
            // one up (decisions #55).
            text.Append(ChatPrompt.NotFoundAnswer);
            yield return new SseItem<object>(new ChatDelta(ChatPrompt.NotFoundAnswer), ChatEvents.Delta);
        }
        else
        {
            // Written as a loop by hand because C# does not allow "yield return" inside a
            // try block that has a catch: the model's failure is caught around the read,
            // and the event is sent outside of it.
            await using var updates = chatClient
                .GetStreamingResponseAsync(answer.Prompt, answer.ModelOptions, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            // Set when the model stopped because it reached the length limit, not because
            // its answer was complete (decisions #64).
            var cutOff = false;

            while (true)
            {
                string piece;

                try
                {
                    if (!await updates.MoveNextAsync())
                    {
                        break;
                    }

                    piece = ChatPrompt.NormalizeBrackets(updates.Current.Text);
                    cutOff |= updates.Current.FinishReason == ChatFinishReason.Length;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "The chat model failed while answering in conversation {ConversationId}.", answer.ConversationId);
                    failed = true;
                    break;
                }

                if (piece.Length > 0)
                {
                    text.Append(piece);
                    yield return new SseItem<object>(new ChatDelta(piece), ChatEvents.Delta);
                }
            }

            if (!failed && text.Length == 0)
            {
                logger.LogError("The chat model returned an empty answer in conversation {ConversationId}.", answer.ConversationId);
                failed = true;
            }

            // An answer that ran into the limit is not a complete answer: most likely the
            // model was repeating itself. It is treated like one that broke off: not
            // stored, and the client is told with an error event (decisions #56, #64).
            if (!failed && cutOff)
            {
                logger.LogWarning(
                    "The chat model reached the answer length limit in conversation {ConversationId}; the answer was not stored.",
                    answer.ConversationId);
                failed = true;
            }
        }

        Guid? messageId = failed ? null : await SaveAsync(answer, numbered, text.ToString(), db, timeProvider, logger, cancellationToken);

        yield return messageId is { } id
            ? new SseItem<object>(new ChatDone(answer.ConversationId, id), ChatEvents.Done)
            : new SseItem<object>(new ChatError("The answer could not be generated."), ChatEvents.Error);
    }

    // Stores the finished answer with the full numbered list it was given (decisions #57).
    // Null when storing fails: the client has seen the text, but without done it knows
    // the answer is not in the history.
    private static async Task<Guid?> SaveAsync(
        Answer answer,
        IReadOnlyList<ChatSource> sources,
        string text,
        AppDbContext db,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = answer.ConversationId,
            Role = MessageRole.Assistant,
            Content = text,
            Sources = sources
                .Select(source => new MessageSource
                {
                    Number = source.Number,
                    DocumentId = source.DocumentId,
                    DocumentTitle = source.DocumentTitle,
                    SectionPath = source.SectionPath,
                    PageNumber = source.PageNumber,
                })
                .ToList(),
            CreatedAt = timeProvider.GetUtcNow(),
        };

        try
        {
            db.Messages.Add(message);
            await db.SaveChangesAsync(cancellationToken);

            return message.Id;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The answer in conversation {ConversationId} could not be stored.", answer.ConversationId);

            return null;
        }
    }

    // Temperature 0: the same question with the same sources should give the same answer.
    // The context size is an Ollama setting (decisions #61); another provider ignores it.
    // The answer's length limit (decisions #64) is common to all providers.
    private static ChatOptions ModelOptions(ChatModelOptions settings) =>
        new ChatOptions { Temperature = 0, MaxOutputTokens = settings.MaxAnswerTokens }
            .AddOllamaOption(OllamaOption.NumCtx, settings.ContextTokens);

    // decisions #57: the first 80 characters of the first question.
    private static string TitleFrom(string question)
    {
        if (question.Length <= ConversationConfiguration.TitleMaxLength)
        {
            return question;
        }

        var title = question[..ConversationConfiguration.TitleMaxLength];

        // Some characters (emoji, for one) take two places in a .NET string; half of one
        // is not valid text.
        return char.IsHighSurrogate(title[^1]) ? title[..^1] : title;
    }

    private sealed record Answer(
        Guid ConversationId,
        IReadOnlyList<SearchResult> Sources,
        IList<ChatMessage> Prompt,
        ChatOptions ModelOptions);
}
