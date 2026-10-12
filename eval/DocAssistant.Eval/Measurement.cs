using System.Text.RegularExpressions;
using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Search;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocAssistant.Eval;

// Asks every question three ways and records where each source turned up:
//
//   keyword  only the full-text search
//   vector   only the meaning-based search
//   hybrid   the whole pipeline behind IChunkRetrieval, as chat will call it
//
// A source counts as found by the first returned chunk that contains its evidence text
// (docs/decisions.md #47). Every method is asked for the same number of chunks, the number
// the pipeline returns (SearchOptions.Results), so the three are compared like for like.
internal static partial class Measurement
{
    public const string Keyword = "keyword";
    public const string Vector = "vector";
    public const string Hybrid = "hybrid";

    public static readonly IReadOnlyList<string> Methods = [Keyword, Vector, Hybrid];

    public static async Task<FormatResult> RunAsync(
        EvalApi api,
        Corpus corpus,
        QuestionSet questions,
        CancellationToken cancellationToken)
    {
        // One scope with the company selected, as a request of one of its users would have.
        await using var scope = api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<ITenantSelector>().Select(corpus.TenantId);

        var keyword = services.GetRequiredService<IKeywordChunkSearch>();
        var vector = services.GetRequiredService<IVectorChunkSearch>();
        var retrieval = services.GetRequiredService<IChunkRetrieval>();
        var limit = services.GetRequiredService<IOptions<SearchOptions>>().Value.Results;

        // The searches return ids; their text is needed to look for the evidence.
        var contents = await services.GetRequiredService<AppDbContext>().Chunks
            .AsNoTracking()
            .ToDictionaryAsync(chunk => chunk.Id, chunk => Normalize(chunk.Content), cancellationToken);

        var results = new List<SourceResult>();

        foreach (var question in questions.Questions.Where(question => question.Sources.Count > 0))
        {
            var found = new Dictionary<string, IReadOnlyList<string>>
            {
                [Keyword] = Texts(await keyword.SearchAsync(question.Text, limit, cancellationToken), contents),
                [Vector] = Texts(await vector.SearchAsync(question.Text, limit, cancellationToken), contents),
                [Hybrid] = (await retrieval.RetrieveAsync(question.Text, cancellationToken))
                    .Select(result => Normalize(result.Content))
                    .ToList(),
            };

            foreach (var source in question.Sources)
            {
                var evidence = Normalize(source.Evidence);

                results.Add(new SourceResult(
                    question.Id,
                    question.Type,
                    source.Document,
                    source.Evidence,
                    InCorpus: contents.Values.Any(text => text.Contains(evidence, StringComparison.Ordinal)),
                    Ranks: Methods.ToDictionary(method => method, method => RankOf(evidence, found[method]))));
            }
        }

        return new FormatResult(corpus.Format, corpus.Documents, contents.Count, limit, results);
    }

    private static IReadOnlyList<string> Texts(IReadOnlyList<RankedChunk> ranking, Dictionary<Guid, string> contents) =>
        ranking.Select(chunk => contents[chunk.ChunkId]).ToList();

    // Position of the first chunk containing the evidence, counted from 1; 0 when none does.
    private static int RankOf(string evidence, IReadOnlyList<string> texts)
    {
        for (var i = 0; i < texts.Count; i++)
        {
            if (texts[i].Contains(evidence, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        return 0;
    }

    // Chunks join paragraphs with blank lines and a PDF breaks lines where the page does;
    // the evidence should match regardless of how the white space fell.
    private static string Normalize(string text) => WhiteSpace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}

// Rank 0 = not among the returned chunks. InCorpus is false when no chunk of the format
// contains the evidence at all: then the question set or the parser needs a look, not the search.
internal sealed record SourceResult(
    string QuestionId,
    string Type,
    string Document,
    string Evidence,
    bool InCorpus,
    IReadOnlyDictionary<string, int> Ranks);

internal sealed record FormatResult(
    string Format,
    int Documents,
    int Chunks,
    int ResultsPerQuestion,
    IReadOnlyList<SourceResult> Sources)
{
    public int FoundWithin(string method, int position) =>
        Sources.Count(source => source.Ranks[method] > 0 && source.Ranks[method] <= position);

    // Mean reciprocal rank: 1 for a source found first, 1/2 for second, 0 when not found.
    public double MeanReciprocalRank(string method) =>
        Sources.Count == 0 ? 0 : Sources.Average(source => source.Ranks[method] > 0 ? 1.0 / source.Ranks[method] : 0);
}
