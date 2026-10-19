using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocAssistant.Api.Modules.Chat;
using DocAssistant.Api.Modules.Search;
using DocAssistant.Shared.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace DocAssistant.Eval;

// Asks the questions through POST /chat, as the chat screen will, and records what can be
// counted about each answer (docs/decisions.md #62): did the model answer or say "not in
// the documents", does the answer carry source numbers, and do they point at a chunk
// that holds the evidence.
//
// Whether an answer is correct is not judged here. Every answer is saved next to the
// expected one for a person to read.
internal static partial class AnswerMeasurement
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<AnswerRun> RunAsync(
        EvalApi api,
        Corpus corpus,
        IReadOnlyList<EvalQuestion> questions,
        string chatModel,
        TimeSpan delay,
        TextWriter progress,
        CancellationToken cancellationToken)
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", corpus.AccessToken);

        // Generating an answer takes seconds; the test client's default would cut off a
        // slow first question, when the model is still being loaded.
        client.Timeout = TimeSpan.FromMinutes(10);

        // The same scope set-up as the search measurement: one company selected.
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantSelector>().Select(corpus.TenantId);
        var retrieval = scope.ServiceProvider.GetRequiredService<IChunkRetrieval>();

        var results = new List<AnswerResult>();

        foreach (var question in questions)
        {
            // The chat stream names its sources but does not carry their text
            // (decisions #56). The text is needed to see which numbers hold the evidence,
            // so the same search is run here first; within one run it returns the same
            // chunks in the same order as the one inside POST /chat.
            var chunks = await retrieval.RetrieveAsync(question.Text, cancellationToken);
            var evidenceAt = EvidenceAt(question, chunks);

            var clock = Stopwatch.StartNew();
            var reply = await AskAsync(client, question.Text, cancellationToken);
            clock.Stop();

            var score = AnswerScoring.Score(reply.Text, reply.Sources.Count, evidenceAt);

            results.Add(new AnswerResult(
                question.Id,
                question.Type,
                question.Text,
                question.ExpectedAnswer,
                reply.Text,
                Failed: reply.Failed,
                HasAnswerInDocuments: question.Sources.Count > 0,
                EvidenceAt: evidenceAt.Order().ToList(),
                // If this is ever false, the numbers above were matched against other
                // chunks than the model saw and the row should not be trusted.
                SourcesMatchSearch: SameSources(reply.Sources, chunks),
                score,
                Math.Round(clock.Elapsed.TotalSeconds, 1)));

            // With a delay (--chat-delay) a run is slow enough to report every question.
            if (results.Count % 10 == 0 || results.Count == questions.Count || delay > TimeSpan.Zero)
            {
                progress.WriteLine($"  {results.Count} of {questions.Count}");
            }

            // An API with a per-minute token limit answers 429 when asked too fast; the
            // pause keeps the run under it.
            if (delay > TimeSpan.Zero && results.Count < questions.Count)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        return new AnswerRun(corpus.Format, chatModel, results);
    }

    // Every type keeps its share: `perType` questions spread evenly over the type's
    // questions, in the set's order, so two sampled runs ask the same questions. The
    // questions without an answer in the documents are few and are what the phase 4 exit
    // criterion is about, so all of them are always asked.
    public static IReadOnlyList<EvalQuestion> Sample(IReadOnlyList<EvalQuestion> questions, int perType) =>
        questions
            .GroupBy(question => question.Type)
            .SelectMany(type =>
            {
                var all = type.ToList();

                if (all[0].Sources.Count == 0 || all.Count <= perType)
                {
                    return all;
                }

                return Enumerable.Range(0, perType).Select(i => all[i * all.Count / perType]);
            })
            .ToList();

    private static async Task<Reply> AskAsync(HttpClient client, string question, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/chat")
        {
            Content = JsonContent.Create(new ChatRequest { Question = question }),
        };

        // Without this the client would wait for the whole body before returning.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"POST /chat answered {(int)response.StatusCode}: {body}");
        }

        IReadOnlyList<ChatSource> sources = [];
        var text = new StringBuilder();
        var done = false;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            switch (item.EventType)
            {
                case ChatEvents.Sources:
                    sources = JsonSerializer.Deserialize<List<ChatSource>>(item.Data, Json) ?? [];
                    break;

                case ChatEvents.Delta:
                    text.Append(JsonSerializer.Deserialize<ChatDelta>(item.Data, Json)?.Text);
                    break;

                case ChatEvents.Done:
                    done = true;
                    break;
            }
        }

        // No done event: the stream ended with error, or broke off.
        return new Reply(sources, text.ToString().Trim(), Failed: !done);
    }

    // The numbers (from 1) of the chunks that hold the evidence of any of the question's
    // sources; matched as the search measurement does (decisions #47).
    private static HashSet<int> EvidenceAt(EvalQuestion question, IReadOnlyList<SearchResult> chunks)
    {
        var evidence = question.Sources.Select(source => Normalize(source.Evidence)).ToList();
        var numbers = new HashSet<int>();

        for (var i = 0; i < chunks.Count; i++)
        {
            var content = Normalize(chunks[i].Content);

            if (evidence.Any(text => content.Contains(text, StringComparison.Ordinal)))
            {
                numbers.Add(i + 1);
            }
        }

        return numbers;
    }

    private static bool SameSources(IReadOnlyList<ChatSource> sources, IReadOnlyList<SearchResult> chunks) =>
        sources.Count == chunks.Count
        && sources.Zip(chunks).All(pair =>
            pair.First.DocumentId == pair.Second.DocumentId
            && pair.First.SectionPath == pair.Second.SectionPath
            && pair.First.PageNumber == pair.Second.PageNumber);

    private static string Normalize(string text) => WhiteSpace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    private sealed record Reply(IReadOnlyList<ChatSource> Sources, string Text, bool Failed);
}

// EvidenceAt is empty for a question whose evidence search did not return: the model was
// never shown the answer, and "not in the documents" is then the right thing to say.
internal sealed record AnswerResult(
    string QuestionId,
    string Type,
    string Question,
    string? ExpectedAnswer,
    string Answer,
    bool Failed,
    bool HasAnswerInDocuments,
    IReadOnlyList<int> EvidenceAt,
    bool SourcesMatchSearch,
    AnswerScore Score,
    double Seconds)
{
    public bool Refused => !Failed && Score.Refused;

    public bool Answered => !Failed && !Score.Refused;

    public bool EvidenceShown => EvidenceAt.Count > 0;
}

internal sealed record AnswerRun(string Format, string ChatModel, IReadOnlyList<AnswerResult> Results);
