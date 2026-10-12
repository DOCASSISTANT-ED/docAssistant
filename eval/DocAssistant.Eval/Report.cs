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
            + $"{unanswerable} without an answer in the documents (not measured: search always returns something).");
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

    // A question with several sources is listed once.
    private static string QuestionIds(IEnumerable<SourceResult> sources) =>
        string.Join(", ", sources.Select(source => source.QuestionId).Distinct());

    // Everything needed to compare two runs question by question.
    public static string Save(RepoPaths paths, QuestionSet questions, string embeddingModel, DateTimeOffset ranAt, IReadOnlyList<FormatResult> formats)
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
        };

        File.WriteAllText(path, JsonSerializer.Serialize(document, Json));

        return path;
    }
}
