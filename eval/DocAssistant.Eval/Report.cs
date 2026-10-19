using System.Globalization;
using System.Text.Json;

namespace DocAssistant.Eval;

internal static class Report
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static void Print(TextWriter output, QuestionSet questions, string embeddingModel, IReadOnlyList<FormatResult> formats)
    {
        var unanswerable = questions.Questions.Count(question => question.Sources.Count == 0);

        output.WriteLine();
        output.WriteLine($"Question set v{questions.Version}: {questions.Questions.Count} questions, "
            + $"{unanswerable} without an answer in the documents (search always returns something; --answers measures them).");
        output.WriteLine($"Embedding model: {embeddingModel}");

        foreach (var format in formats)
        {
            var last = format.ResultsPerQuestion;
            int[] positions = [1, Math.Min(5, last), last];

            output.WriteLine();
            output.WriteLine($"== {format.Format.ToUpperInvariant()}: {format.Documents} documents, {format.Chunks} chunks, "
                + $"{format.Sources.Count} sources, {last} chunks per question ==");
            output.WriteLine();
            output.WriteLine($"  {"method",-10}{string.Concat(positions.Distinct().Select(p => $"{"top " + p,8}"))}{"MRR",8}");

            foreach (var method in Measurement.Methods)
            {
                var found = string.Concat(positions.Distinct().Select(p => $"{format.FoundWithin(method, p),8}"));
                output.WriteLine($"  {method,-10}{found}{format.MeanReciprocalRank(method).ToString("F3", CultureInfo.InvariantCulture),8}");
            }

            output.WriteLine();
            output.WriteLine($"  Found within the {last} returned chunks, by question type:");
            output.WriteLine($"  {"type",-16}{"sources",8}{string.Concat(Measurement.Methods.Select(m => $"{m,9}"))}");

            foreach (var type in format.Sources.GroupBy(source => source.Type))
            {
                var byMethod = string.Concat(Measurement.Methods.Select(
                    method => $"{type.Count(source => source.Ranks[method] > 0),9}"));
                output.WriteLine($"  {type.Key,-16}{type.Count(),8}{byMethod}");
            }

            // The phase 3 exit criterion compares these two (PLANNING.md).
            var hybrid = format.MeanReciprocalRank(Measurement.Hybrid);
            var vector = format.MeanReciprocalRank(Measurement.Vector);
            var verdict = hybrid > vector ? "better than" : hybrid < vector ? "worse than" : "equal to";
            output.WriteLine();
            output.WriteLine($"  Hybrid is {verdict} vector search alone (MRR "
                + $"{hybrid.ToString("F3", CultureInfo.InvariantCulture)} against {vector.ToString("F3", CultureInfo.InvariantCulture)}).");

            var missed = format.Sources.Where(source => source.Ranks[Measurement.Hybrid] == 0).ToList();
            if (missed.Count > 0)
            {
                output.WriteLine($"  Sources hybrid did not find ({missed.Count}), by question: {QuestionIds(missed)}");
            }

            // No chunk holds the evidence at all: the parser or the question set, not the search.
            var absent = format.Sources.Where(source => !source.InCorpus).ToList();
            if (absent.Count > 0)
            {
                output.WriteLine($"  Evidence in no chunk of this format ({absent.Count}), by question: {QuestionIds(absent)}");
            }
        }
    }

    // --conversations: the last turn of every conversation, by kind. "evidence among the
    // sources" says whether the chat brought the right chunk to the model at all; the rest
    // is what the model did with it, read as in the answer measurement.
    public static void PrintConversations(TextWriter output, string chatModel, IReadOnlyList<ConversationResult> results)
    {
        output.WriteLine();
        output.WriteLine($"== Conversations (DOCX, chat model {chatModel}): {results.Count}, last turn measured ==");
        output.WriteLine();
        output.WriteLine($"  {"kind",-12}{"total",7}{"failed",8}{"evidence",10}{"refused",9}{"numbered",10}{"cites ev.",11}");

        foreach (var kind in results.GroupBy(result => result.Kind))
        {
            var ran = kind.Where(result => !result.Failed).ToList();
            output.WriteLine($"  {kind.Key,-12}{kind.Count(),7}{kind.Count(result => result.Failed),8}"
                + $"{ran.Count(result => result.EvidenceShown),10}{ran.Count(result => result.Refused),9}"
                + $"{ran.Count(result => result.Score.Cited.Count > 0),10}{ran.Count(result => result.Score.CitesEvidence),11}");
        }

        output.WriteLine();
        output.WriteLine("  evidence: the evidence was among the sources of the last answer; cites ev.: the answer cites it.");

        var missed = results.Where(result => !result.Failed && !result.EvidenceShown).Select(result => result.ConversationId).ToList();
        if (missed.Count > 0)
        {
            output.WriteLine($"  Evidence not among the sources: {string.Join(", ", missed)}");
        }

        var failed = results.Where(result => result.Failed).Select(result => result.ConversationId).ToList();
        if (failed.Count > 0)
        {
            output.WriteLine($"  Failed: {string.Join(", ", failed)}");
        }

        output.WriteLine("  Not measured: whether an answer is correct. The details file has every turn and the expected answer.");
    }

    // docs/decisions.md #62. The two phase 4 exit criteria (PLANNING.md) are the first
    // block and the "with a source number" line.
    public static void PrintAnswers(TextWriter output, AnswerRun run)
    {
        var all = run.Results;
        var withoutAnswer = all.Where(result => !result.HasAnswerInDocuments).ToList();
        var shown = all.Where(result => result.HasAnswerInDocuments && result.EvidenceShown).ToList();
        var notShown = all.Where(result => result.HasAnswerInDocuments && !result.EvidenceShown).ToList();
        var answered = shown.Where(result => result.Answered).ToList();
        var saidNotFound = $"said \"{AnswerScoring.RefusalPhrase}\"";

        output.WriteLine();
        output.WriteLine($"== Answers ({run.Format.ToUpperInvariant()}, chat model {run.ChatModel}): {all.Count} questions ==");

        output.WriteLine();
        output.WriteLine($"  Without an answer in the documents: {withoutAnswer.Count}");
        Line(output, 4, saidNotFound, withoutAnswer.Where(result => result.Refused));
        Line(output, 4, "answered anyway", withoutAnswer.Where(result => result.Answered), listIds: true);
        Line(output, 4, "failed", withoutAnswer.Where(result => result.Failed), listIds: true);

        output.WriteLine();
        output.WriteLine($"  With an answer, and search returned the evidence: {shown.Count}");
        Line(output, 4, "answered", answered);
        Line(output, 6, "with a source number", answered.Where(result => result.Score.Cited.Count > 0));
        Line(output, 6, "a number points at the evidence", answered.Where(result => result.Score.CitesEvidence));
        Line(output, 4, saidNotFound, shown.Where(result => result.Refused), listIds: true);
        Line(output, 4, "failed", shown.Where(result => result.Failed), listIds: true);

        // The model was never shown the answer; refusing is the right thing to do here.
        output.WriteLine();
        output.WriteLine($"  With an answer, but search did not return the evidence: {notShown.Count}");
        Line(output, 4, saidNotFound, notShown.Where(result => result.Refused));
        Line(output, 4, "answered anyway", notShown.Where(result => result.Answered), listIds: true);

        output.WriteLine();
        output.WriteLine("  Form of the source numbers, over all answers:");
        Line(output, 4, "a number that is not in the list", all.Where(result => result.Score.NumberNotInList), listIds: true);
        Line(output, 4, "several numbers in one bracket, [1, 3]", all.Where(result => result.Score.ListInOneBracket), listIds: true);
        Line(output, 4, "an answer and \"bulunamadı\" together", all.Where(result => result.Score.AnswersAndRefuses), listIds: true);

        output.WriteLine();
        output.WriteLine("  Questions whose evidence search returned, by type:");
        output.WriteLine($"  {"type",-16}{"questions",10}{"answered",10}{"numbered",10}{"evidence",10}{"refused",9}");

        foreach (var type in shown.GroupBy(result => result.Type))
        {
            var typeAnswered = type.Where(result => result.Answered).ToList();

            output.WriteLine($"  {type.Key,-16}{type.Count(),10}{typeAnswered.Count,10}"
                + $"{typeAnswered.Count(result => result.Score.Cited.Count > 0),10}"
                + $"{typeAnswered.Count(result => result.Score.CitesEvidence),10}"
                + $"{type.Count(result => result.Refused),9}");
        }

        var mismatched = all.Where(result => !result.SourcesMatchSearch).ToList();
        if (mismatched.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"  WARNING: the chat's sources differed from the search run here for {mismatched.Count} questions; "
                + $"their source numbers were matched against other chunks: {string.Join(", ", mismatched.Select(result => result.QuestionId))}");
        }

        output.WriteLine();
        output.WriteLine($"  Median time per question: {Median(all.Select(result => result.Seconds)).ToString("F1", CultureInfo.InvariantCulture)} s.");
        output.WriteLine("  Not measured: whether an answer is correct. The details file has every answer next to the expected one.");
    }

    private static void Line(TextWriter output, int indent, string label, IEnumerable<AnswerResult> results, bool listIds = false)
    {
        var list = results.ToList();
        var ids = listIds && list.Count > 0 ? "   " + string.Join(", ", list.Select(result => result.QuestionId)) : string.Empty;

        output.WriteLine($"{new string(' ', indent)}{label.PadRight(44 - indent)}{list.Count,5}{ids}");
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();

        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    // A question with several sources is listed once.
    private static string QuestionIds(IEnumerable<SourceResult> sources) =>
        string.Join(", ", sources.Select(source => source.QuestionId).Distinct());

    // Everything needed to compare two runs question by question.
    public static string Save(
        RepoPaths paths,
        QuestionSet questions,
        string embeddingModel,
        DateTimeOffset ranAt,
        IReadOnlyList<FormatResult> formats,
        AnswerRun? answers,
        IReadOnlyList<ConversationResult>? conversations)
    {
        Directory.CreateDirectory(paths.Results);
        var path = Path.Combine(paths.Results, $"{ranAt:yyyyMMdd-HHmmss}.json");

        var document = new
        {
            RanAt = ranAt,
            QuestionSetVersion = questions.Version,
            EmbeddingModel = embeddingModel,
            Formats = formats.Select(format => new
            {
                format.Format,
                format.Documents,
                format.Chunks,
                format.ResultsPerQuestion,
                Summary = Measurement.Methods.ToDictionary(method => method, method => new
                {
                    Top1 = format.FoundWithin(method, 1),
                    Top5 = format.FoundWithin(method, Math.Min(5, format.ResultsPerQuestion)),
                    TopAll = format.FoundWithin(method, format.ResultsPerQuestion),
                    Mrr = Math.Round(format.MeanReciprocalRank(method), 4),
                }),
                format.Sources,
            }),

            // Present only when the run measured answers (--answers): every answer next
            // to the expected one, to be read by a person.
            Answers = answers,

            // Present only with --conversations: every turn of every conversation.
            Conversations = conversations,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(document, Json));

        return path;
    }
}
