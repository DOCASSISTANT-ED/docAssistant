using System.Text.RegularExpressions;

namespace DocAssistant.Eval;

// --typos: one spelling mistake in every question, to see how much a typo costs the search.
//
// The mistake goes into the question's longest word, because that is usually the word the
// search relies on most. Only words of letters count, at least five of them; a number such
// as "1.500" is left alone. A question without such a word stays as it is.
//
// It goes at the middle letter (people seldom get the first letter wrong) and is one of
// three kinds, taken in turn from the question's id:
//
//   delete  a letter is missing      sevkiyat -> sevkiyt
//   swap    two letters swapped      limitler -> limtiler
//   double  a letter typed twice     kullanım -> kullaanım
//
// The same question always gets the same mistake, so two runs can be compared. The kind is
// not chosen with string.GetHashCode: .NET gives that a different value in every process.
internal static partial class Typos
{
    // The questions that changed, by id, with their new text.
    public static IReadOnlyDictionary<string, string> Make(QuestionSet questions)
    {
        var changed = new Dictionary<string, string>();

        foreach (var question in questions.Questions)
        {
            var text = Introduce(question.Id, question.Text);

            if (text != question.Text)
            {
                changed[question.Id] = text;
            }
        }

        return changed;
    }

    public static QuestionSet Apply(QuestionSet questions, IReadOnlyDictionary<string, string> typos) =>
        questions with
        {
            Questions = questions.Questions
                .Select(question => typos.TryGetValue(question.Id, out var text) ? question with { Text = text } : question)
                .ToList(),
        };

    public static string Introduce(string id, string text)
    {
        // The longest word; on a tie the first, because MaxBy keeps the first it saw.
        var word = Word().Matches(text).MaxBy(match => match.Length);

        if (word is null)
        {
            return text;
        }

        var misspelled = Misspell(word.Value, id.Sum(character => character) % 3);

        return string.Concat(text.AsSpan(0, word.Index), misspelled, text.AsSpan(word.Index + word.Length));
    }

    private static string Misspell(string word, int kind)
    {
        var middle = word.Length / 2;

        // Swapping two equal letters would change nothing; such a word loses a letter instead.
        if (kind == 1 && word[middle] != word[middle + 1])
        {
            return string.Concat(word.AsSpan(0, middle), [word[middle + 1], word[middle]], word.AsSpan(middle + 2));
        }

        return kind == 2
            ? word.Insert(middle, word[middle].ToString())
            : word.Remove(middle, 1);
    }

    // At least five letters, with no letter or digit right before or after.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])\p{L}{5,}(?![\p{L}\p{N}])")]
    private static partial Regex Word();
}
