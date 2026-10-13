using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace DocAssistant.IntegrationTests.Chat;

// Stands in for the chat model (docs/decisions.md #10): writes the pieces the test chose,
// one update per piece, and remembers what it was asked. No model runs in tests.
internal sealed class FakeChatModel : IChatClient
{
    private readonly List<ModelCall> _calls = [];

    // What the model writes, in the pieces it arrives in.
    public IReadOnlyList<string> Pieces { get; set; } = ["Cevap."];

    // When set, thrown after FailAfterPieces pieces have been written.
    public Exception? Failure { get; set; }

    public int FailAfterPieces { get; set; }

    // Every request the endpoint made, oldest first.
    public IReadOnlyList<ModelCall> Calls => _calls;

    public void Answer(params string[] pieces)
    {
        Pieces = pieces;
        Failure = null;
    }

    public void FailWith(Exception failure, int afterPieces = 0)
    {
        Failure = failure;
        FailAfterPieces = afterPieces;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _calls.Add(new ModelCall(messages.ToList(), options));

        var written = 0;

        foreach (var piece in Pieces)
        {
            if (Failure is not null && written == FailAfterPieces)
            {
                throw Failure;
            }

            // Lets the endpoint send the previous piece before the next one is produced,
            // as a real model's pauses do.
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            written++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
        }

        if (Failure is not null && written == FailAfterPieces)
        {
            throw Failure;
        }
    }

    // The endpoint streams (#56); a test that ends up here has found a change worth knowing about.
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The chat endpoint is expected to ask for a streamed answer.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed record ModelCall(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);
