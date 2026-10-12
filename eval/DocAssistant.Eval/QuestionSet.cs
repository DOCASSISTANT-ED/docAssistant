using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocAssistant.Eval;

// eval/questions.json (docs/decisions.md #40, #45, #47; the fields are described in
// eval/README.md).
internal sealed record QuestionSet(int Version, IReadOnlyList<EvalQuestion> Questions)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static QuestionSet Load(string path)
    {
        using var file = File.OpenRead(path);

        return JsonSerializer.Deserialize<QuestionSet>(file, Json)
            ?? throw new InvalidDataException($"{path} is empty.");
    }
}

internal sealed record EvalQuestion(
    string Id,
    string Type,
    [property: JsonPropertyName("question")] string Text,
    string? ExpectedAnswer,
    IReadOnlyList<EvalSource> Sources);

internal sealed record EvalSource(string Document, string? Section, int? Page, string Evidence);
