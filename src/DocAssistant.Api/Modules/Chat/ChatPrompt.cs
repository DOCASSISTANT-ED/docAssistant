using System.Text;
using System.Text.RegularExpressions;
using DocAssistant.Api.Modules.Search;
using Microsoft.Extensions.AI;

namespace DocAssistant.Api.Modules.Chat;

// Turns a question, its sources and the conversation so far into what the model reads
// (docs/decisions.md #55). Pure text work: no database, no model.
//
// The wording is a starting point (decisions #61) and is kept short on purpose: with the
// development model, longer and more detailed instructions gave worse answers. The final
// prompt is designed together and measured with the evaluation set.
public static partial class ChatPrompt
{
    // The answer when the documents do not hold one. The instructions ask the model for
    // exactly this sentence, and the endpoint sends it itself when search finds nothing.
    public const string NotFoundAnswer = "Bu bilgi belgelerde bulunamadı.";

    // The line breaks of a raw string are those of the source file, and on Windows Git
    // checks this file out with Windows line breaks (CR LF). They are normalized to LF so
    // that the model reads the same instructions on every machine: with CR LF the
    // development model answered questions with the not-found sentence that it answers
    // correctly with LF.
    private static readonly string Instructions =
        $"""
        Sen bir şirketin belgelerine dayanarak soru cevaplayan bir asistansın.

        Kurallar:
        1. Yalnızca sana verilen numaralı kaynaklardaki bilgiyi kullan. Kendi bilgini ekleme, tahmin yürütme.
        2. Yazdığın her bilginin sonuna, o bilgiyi aldığın kaynağın numarasını köşeli parantezle yaz. Örnek: Yıllık izin 14 gündür [2].
        3. Sorunun cevabı kaynaklarda yoksa yalnızca şu cümleyi yaz, başka hiçbir şey ekleme ve bu cümleyi çevirme: {NotFoundAnswer}
        4. Cevabı "Soru:" satırındaki sorunun yazıldığı dilde ver; kaynaklar başka dilde olsa da. Kısa ve doğrudan cevap ver.
        """.ReplaceLineEndings("\n");

    // The instructions of the extra request that turns a follow-up question into one that
    // stands on its own (decisions #69). Its answer is used for searching, and shown to the
    // answering model under the user's own question.
    private static readonly string RewriteInstructions =
        """
        Bir sohbetin son sorusunu, sohbeti görmeyen biri de anlayacak şekilde tek bir soru olarak yeniden yaz.

        Kurallar:
        1. Kullanıcının sohbette verdiği ve soruyla ilgili bilgileri (çalışma süresi, görevi, tutar, yer gibi) soruya ekle.
        2. "bu", "o", "peki ya" gibi önceki mesajlara gönderme yapan sözleri, gönderme yaptıkları şeyle değiştir.
        3. Son soru zaten tek başına anlaşılıyorsa ya da önceki mesajlarla ilgisi yoksa olduğu gibi yaz.
        4. Soruyu cevaplama. Yalnızca yeniden yazılmış soruyu yaz, başka hiçbir şey ekleme.
        """.ReplaceLineEndings("\n");

    // A rewritten question longer than this is taken to be something other than a question
    // (an answer, an explanation), and the user's own question is searched instead.
    public const int RewrittenQuestionMaxLength = 500;

    // Which earlier messages the model gets (decisions #69): the user's messages first,
    // newest first, then the assistant's answers in what is left of the budget; returned
    // in the order they were written. Stops at the first message that does not fit, so
    // what is sent is always the most recent part of each kind.
    // all: every earlier message of the conversation, oldest first.
    public static IReadOnlyList<Message> SelectHistory(IReadOnlyList<Message> all, int budgetTokens)
    {
        var chosen = new HashSet<Message>();
        var left = budgetTokens;

        foreach (var role in new[] { MessageRole.User, MessageRole.Assistant })
        {
            foreach (var message in all.Where(m => m.Role == role).Reverse())
            {
                var tokens = EstimateTokens(message.Content);

                if (tokens > left)
                {
                    break;
                }

                chosen.Add(message);
                left -= tokens;
            }
        }

        return all.Where(chosen.Contains).ToList();
    }

    // A model's own tokenizer is not at hand; a token of Turkish text is three to four
    // letters, so a third of the length errs on the side of counting too many.
    public static int EstimateTokens(string text) => (text.Length + 2) / 3;

    // Two worked examples, given as earlier turns of the rewrite request: the development
    // model followed the instructions alone poorly (it answered the question, or wrote a
    // label in front of it) and copies an example's form far more reliably. The examples'
    // facts are made up and appear in no document, so they cannot leak into an answer.
    private static readonly (string Conversation, string Rewritten)[] RewriteExamples =
    [
        (
            "Sohbet:\n\nKullanıcı: Şirkette 2 yıldır çalışıyorum.\n\nAsistan: Anlaşıldı.\n\nKullanıcı: Kantin kaçta açılıyor?\n\nAsistan: Kantin 08:00'de açılır.\n\nSon soru: Benim süreme göre kaç gün izin hakkım var?",
            "Şirkette 2 yıldır çalışan birinin kaç gün izin hakkı vardır?"
        ),
        (
            "Sohbet:\n\nKullanıcı: Ankara'ya kargo kaç günde gider?\n\nAsistan: Ankara'ya kargo 2 iş gününde gider.\n\nSon soru: Peki adalara?",
            "Adalara kargo kaç günde gider?"
        ),
    ];

    // The request that rewrites a follow-up question: the conversation as text, then the
    // last question. history: oldest first, as SelectHistory returns it.
    public static IList<ChatMessage> BuildRewrite(IReadOnlyList<Message> history, string question)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, RewriteInstructions) };

        foreach (var (conversation, rewritten) in RewriteExamples)
        {
            messages.Add(new ChatMessage(ChatRole.User, conversation));
            messages.Add(new ChatMessage(ChatRole.Assistant, rewritten));
        }

        var text = new StringBuilder("Sohbet:\n\n");

        foreach (var message in history)
        {
            text.Append(message.Role == MessageRole.User ? "Kullanıcı: " : "Asistan: ")
                .Append(WithoutSourceNumbers(message.Content))
                .Append("\n\n");
        }

        text.Append("Son soru: ").Append(question);
        messages.Add(new ChatMessage(ChatRole.User, text.ToString()));

        return messages;
    }

    // The rewritten question as the model wrote it, or null when it cannot be used. Models
    // sometimes put a label in front of it ("Yeniden yazılan soru:"), on the same line or
    // the one before, and sometimes write an answer instead: a line that does not end in
    // a question mark is not a question, and the user's own question is searched instead.
    public static string? CleanRewrite(string? text)
    {
        var lines = text?
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim('"', '\'', '“', '”', '«', '»', '*').Trim())
            .Where(line => line.Length > 0)
            .ToList() ?? [];

        // A label alone on its line: the question is on the next one.
        var line = lines.FirstOrDefault(line => !line.EndsWith(':'));

        if (line is null)
        {
            return null;
        }

        // A label on the same line: what follows its colon.
        var colon = line.IndexOf(':');
        if (colon >= 0 && line[..colon].Contains("soru", StringComparison.OrdinalIgnoreCase))
        {
            line = line[(colon + 1)..].Trim('"', '\'', '“', '”', '«', '»', '*', ' ');
        }

        return line.Length is 0 or > RewrittenQuestionMaxLength || !line.EndsWith('?') ? null : line;
    }

    // sources: in the order search returned them; the first becomes [1].
    // history: earlier messages of the same conversation, oldest first.
    // standaloneQuestion: the follow-up question rewritten to stand on its own (decisions
    // #69); shown under the user's own words when it says something else.
    public static IList<ChatMessage> Build(
        IReadOnlyList<SearchResult> sources,
        IReadOnlyList<Message> history,
        string question,
        string? standaloneQuestion = null)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, Instructions) };

        foreach (var message in history)
        {
            messages.Add(message.Role == MessageRole.User
                ? new ChatMessage(ChatRole.User, message.Content)
                // An earlier answer's numbers pointed at that question's sources. Here
                // they would look like references to the new ones, so they are removed.
                : new ChatMessage(ChatRole.Assistant, WithoutSourceNumbers(message.Content)));
        }

        var text = new StringBuilder("Kaynaklar:\n\n");

        for (var i = 0; i < sources.Count; i++)
        {
            // The chunk's content starts with its context header: the document's title
            // and section path (decisions #27).
            text.Append('[').Append(i + 1).Append("]\n").Append(sources[i].Content).Append("\n\n");
        }

        text.Append("Soru: ").Append(question);

        if (standaloneQuestion is not null && !string.Equals(standaloneQuestion, question, StringComparison.OrdinalIgnoreCase))
        {
            text.Append("\nAçık hâli: ").Append(standaloneQuestion);
        }

        messages.Add(new ChatMessage(ChatRole.User, text.ToString()));

        return messages;
    }

    private static string WithoutSourceNumbers(string answer) => SourceNumber().Replace(answer, string.Empty).Trim();

    // Source numbers are written "[3]" (decisions #56), but some models write them with
    // other brackets: 【3】 (gpt-oss) or the full-width ［3］ (decisions #68). The answer
    // arrives in pieces and a number can be split between two of them ("…【" and "3】"),
    // so each bracket is replaced on its own rather than the whole "【3】".
    public static string NormalizeBrackets(string piece) =>
        piece.IndexOfAny(['【', '】', '［', '］']) < 0
            ? piece
            : piece.Replace('【', '[').Replace('】', ']').Replace('［', '[').Replace('］', ']');

    // "[3]", and also "[1, 3]": the prompt asks for one number per bracket (decisions #56),
    // but models sometimes write two in one.
    [GeneratedRegex(@"\s*\[\d+(\s*,\s*\d+)*\]")]
    private static partial Regex SourceNumber();
}
