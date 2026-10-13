using DocAssistant.Api.Modules.Search;

namespace DocAssistant.IntegrationTests.Chat;

// Stands in for search (docs/decisions.md #48): returns the sources the test chose and
// remembers the questions it was asked. Search has its own tests; these are about what
// chat does with the result.
internal sealed class FakeChunkRetrieval : IChunkRetrieval
{
    private readonly List<string> _queries = [];

    // What search finds, best first. Empty: nothing in the documents.
    public IReadOnlyList<SearchResult> Results { get; set; } = [];

    // Every query the endpoint searched with, oldest first.
    public IReadOnlyList<string> Queries => _queries;

    public Task<IReadOnlyList<SearchResult>> RetrieveAsync(string query, CancellationToken cancellationToken)
    {
        _queries.Add(query);

        return Task.FromResult(Results);
    }

    // A source with new ids. The document does not exist in the database: chat stores
    // what search returned and does not look the document up (#57).
    public static SearchResult Source(
        string documentTitle,
        string content,
        string? sectionPath = null,
        int? pageNumber = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), documentTitle, sectionPath, pageNumber, content, Score: 1);
}
