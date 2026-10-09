using System.ComponentModel.DataAnnotations;

namespace DocAssistant.Api.Modules.Search;

// How many chunks each step passes on (docs/decisions.md #48). Starting values; the
// evaluation set decides whether they change, so they come from configuration ("Search").
public sealed class SearchOptions
{
    public const string SectionName = "Search";

    // Taken from each search (keyword and vector) before fusion.
    [Range(1, 200)]
    public int CandidatesPerSearch { get; set; } = 30;

    // Fused candidates handed to the reranker.
    [Range(1, 200)]
    public int CandidatesForReranking { get; set; } = 30;

    // Returned to the caller: the chunks given to the chat model in phase 4.
    [Range(1, 50)]
    public int Results { get; set; } = 8;
}
