using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DocAssistant.Api.Modules.Chat;

namespace DocAssistant.IntegrationTests.Chat;

// The answer stream of POST /chat, read to its end and split into its events
// (docs/decisions.md #56).
internal sealed class ChatAnswer
{
    private ChatAnswer(IReadOnlyList<ChatEvent> events)
    {
        Events = events;
    }

    // Every event in the order it arrived.
    public IReadOnlyList<ChatEvent> Events { get; }

    public IEnumerable<string> EventTypes => Events.Select(e => e.Type);

    // The "sources" event's list; fails the test when there is not exactly one.
    public IReadOnlyList<ChatSource> Sources =>
        Read<List<ChatSource>>(Assert.Single(Events, e => e.Type == ChatEvents.Sources));

    // The pieces of the answer as they arrived.
    public IReadOnlyList<string> Deltas =>
        Events.Where(e => e.Type == ChatEvents.Delta).Select(e => Read<ChatDelta>(e).Text).ToList();

    // The whole answer text.
    public string Text => string.Concat(Deltas);

    // The "done" event; fails the test when there is not exactly one.
    public ChatDone Done => Read<ChatDone>(Assert.Single(Events, e => e.Type == ChatEvents.Done));

    // The "error" event; fails the test when there is not exactly one.
    public ChatError Error => Read<ChatError>(Assert.Single(Events, e => e.Type == ChatEvents.Error));

    public static async Task<ChatAnswer> ReadAsync(HttpResponseMessage response)
    {
        var events = new List<ChatEvent>();

        await foreach (var chatEvent in StreamAsync(response))
        {
            events.Add(chatEvent);
        }

        return new ChatAnswer(events);
    }

    // The events one at a time, each as soon as it arrives: for tests about what has been
    // sent while the answer is still being written.
    public static async IAsyncEnumerable<ChatEvent> StreamAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);

        await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            yield return new ChatEvent(item.EventType, item.Data);
        }
    }

    // The data of an event as raw JSON, for tests about the exact field names.
    public static JsonElement Json(ChatEvent chatEvent) => JsonDocument.Parse(chatEvent.Data).RootElement;

    // The API writes JSON the web way (camelCase), as every other endpoint does.
    private static T Read<T>(ChatEvent chatEvent) =>
        JsonSerializer.Deserialize<T>(chatEvent.Data, JsonSerializerOptions.Web)
        ?? throw new InvalidOperationException($"The data of the '{chatEvent.Type}' event is empty.");
}

// One event as it was sent: its name and its data, still as JSON text.
internal sealed record ChatEvent(string Type, string Data);
