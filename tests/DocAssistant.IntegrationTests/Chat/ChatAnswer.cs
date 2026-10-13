using System.Net.ServerSentEvents;
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
        await using var body = await response.Content.ReadAsStreamAsync();

        var events = new List<ChatEvent>();

        await foreach (var item in SseParser.Create(body).EnumerateAsync())
        {
            events.Add(new ChatEvent(item.EventType, item.Data));
        }

        return new ChatAnswer(events);
    }

    // The API writes JSON the web way (camelCase), as every other endpoint does.
    private static T Read<T>(ChatEvent chatEvent) =>
        JsonSerializer.Deserialize<T>(chatEvent.Data, JsonSerializerOptions.Web)
        ?? throw new InvalidOperationException($"The data of the '{chatEvent.Type}' event is empty.");
}

// One event as it was sent: its name and its data, still as JSON text.
internal sealed record ChatEvent(string Type, string Data);
