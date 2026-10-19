using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using DocAssistant.Api.Data;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocAssistant.Eval;

// --conversations: short conversations from eval/conversations.json, asked turn by turn
// in one conversation through POST /chat, as a user would. Only the last turn is
// measured; it leans on the earlier ones ("the time I mentioned at the start", "and the
// villages?"), or, for the "switch" kind, does not and must not be thrown off by them.
//
// The answer measurement (AnswerMeasurement) finds the evidence by running the search
// itself with the question. A follow-up question may be searched with other words than
// the user's, so here the evidence is looked up in the sources the chat itself reported:
// a number holds the evidence when a chunk of that document and section contains it.
internal static class ConversationMeasurement
{
    public static async Task<IReadOnlyList<ConversationResult>> RunAsync(
        EvalApi api,
        Corpus corpus,
        ConversationSet conversations,
        TimeSpan delay,
        TextWriter progress,
        CancellationToken cancellationToken)
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", corpus.AccessToken);
        client.Timeout = TimeSpan.FromMinutes(10);

        // The company's chunks, to look up which sources hold the evidence.
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantSelector>().Select(corpus.TenantId);
        var chunks = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Chunks
            .AsNoTracking()
            .Select(chunk => new { chunk.DocumentId, chunk.SectionPath, chunk.PageNumber, chunk.Content })
            .ToListAsync(cancellationToken);

        var results = new List<ConversationResult>();

        foreach (var conversation in conversations.Conversations)
        {
            Guid? conversationId = null;
            var turns = new List<ConversationTurn>();
            AnswerMeasurement.Reply? last = null;
            var clock = new Stopwatch();

            foreach (var question in conversation.Turns)
            {
                clock.Restart();
                last = await AnswerMeasurement.AskAsync(client, question, cancellationToken, conversationId);
                clock.Stop();

                turns.Add(new ConversationTurn(question, last.Text, last.Failed));

                // A failed turn is not saved by the app (decisions #56); later turns would
                // then lean on a reply that is not there, so the conversation stops.
                if (last.Failed)
                {
                    break;
                }

                conversationId = last.ConversationId;

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }

            var evidence = conversation.Sources.Select(source => AnswerMeasurement.Normalize(source.Evidence)).ToList();
            var evidenceAt = new HashSet<int>();

            foreach (var source in last!.Sources)
            {
                var holdsEvidence = chunks
                    .Where(chunk => chunk.DocumentId == source.DocumentId
                        && chunk.SectionPath == source.SectionPath
                        && chunk.PageNumber == source.PageNumber)
                    .Any(chunk => evidence.Any(text =>
                        AnswerMeasurement.Normalize(chunk.Content).Contains(text, StringComparison.Ordinal)));

                if (holdsEvidence)
                {
                    evidenceAt.Add(source.Number);
                }
            }

            // Measured only when every turn got an answer.
            var completed = turns.Count == conversation.Turns.Count;

            results.Add(new ConversationResult(
                conversation.Id,
                conversation.Kind,
                conversation.ExpectedAnswer,
                turns,
                Failed: !completed || last.Failed,
                EvidenceAt: evidenceAt.Order().ToList(),
                AnswerScoring.Score(last.Text, last.Sources.Count, evidenceAt),
                Math.Round(clock.Elapsed.TotalSeconds, 1)));

            progress.WriteLine($"  {results.Count} of {conversations.Conversations.Count}");
        }

        return results;
    }
}

// eval/conversations.json (described in eval/README.md).
internal sealed record ConversationSet(int Version, IReadOnlyList<EvalConversation> Conversations)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static ConversationSet Load(string path)
    {
        using var file = File.OpenRead(path);

        return JsonSerializer.Deserialize<ConversationSet>(file, Json)
            ?? throw new InvalidDataException($"{path} is empty.");
    }
}

// kind: "fact" (the last turn needs something the user said earlier), "reference" (it
// points back at the previous question) or "switch" (it stands on its own).
internal sealed record EvalConversation(
    string Id,
    string Kind,
    IReadOnlyList<string> Turns,
    string ExpectedAnswer,
    IReadOnlyList<EvalSource> Sources);

internal sealed record ConversationTurn(string Question, string Answer, bool Failed);

// EvidenceAt: the numbers of the last answer's sources that hold the evidence; empty when
// the chat did not bring the evidence to the model at all.
internal sealed record ConversationResult(
    string ConversationId,
    string Kind,
    string ExpectedAnswer,
    IReadOnlyList<ConversationTurn> Turns,
    bool Failed,
    IReadOnlyList<int> EvidenceAt,
    AnswerScore Score,
    double Seconds)
{
    public bool EvidenceShown => EvidenceAt.Count > 0;

    public bool Refused => !Failed && Score.Refused;
}
