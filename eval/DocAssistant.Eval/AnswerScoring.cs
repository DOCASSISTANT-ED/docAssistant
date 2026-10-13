using System.Text.RegularExpressions;

namespace DocAssistant.Eval;

// What can be read off an answer's text without judging whether it is right
// (docs/decisions.md #62). Pure text work, so it can be unit tested without a model.
internal static partial class AnswerScoring
{
    // The words every "not in the documents" answer must contain: the sentence the
    // prompt asks for (ChatPrompt.NotFoundAnswer), without its first word and full stop,
    // so that "Bu bilgi belgelerde bulunamadı" in other casing or wording still counts.
    public const string RefusalPhrase = "belgelerde bulunamadı";

    public static AnswerScore Score(string answer, int sourceCount, IReadOnlySet<int> evidenceAt)
    {
        var cited = new SortedSet<int>();
        var listInOneBracket = false;

        foreach (Match bracket in Brackets().Matches(answer))
        {
            var numbers = Numbers().Matches(bracket.Value).Select(number => int.Parse(number.Value)).ToList();
            cited.UnionWith(numbers);

            // decisions #56: one number per bracket, "[1][3]". "[1, 3]" is not that form.
            listInOneBracket |= numbers.Count > 1;
        }

        var saysNotFound = answer.Contains(RefusalPhrase, StringComparison.OrdinalIgnoreCase);

        return new AnswerScore(
            // A refusal names no source. An answer that cites sources and then adds
            // "bulunamadı" has not refused; it contradicts itself (counted below).
            Refused: saysNotFound && cited.Count == 0,
            Cited: cited.ToList(),
            CitesEvidence: cited.Overlaps(evidenceAt),
            NumberNotInList: cited.Any(number => number < 1 || number > sourceCount),
            ListInOneBracket: listInOneBracket,
            AnswersAndRefuses: saysNotFound && cited.Count > 0);
    }

    // "[2]", "[1, 3]", "[1,3]"; not "[a]" or "[2024 yılı]".
    [GeneratedRegex(@"\[\s*\d+(\s*,\s*\d+)*\s*\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();
}

// Refused: the answer says the documents do not hold it and cites nothing.
// Cited: the source numbers in the answer, each once, ascending.
// CitesEvidence: at least one cited number is a chunk that holds the question's evidence.
internal sealed record AnswerScore(
    bool Refused,
    IReadOnlyList<int> Cited,
    bool CitesEvidence,
    bool NumberNotInList,
    bool ListInOneBracket,
    bool AnswersAndRefuses);
