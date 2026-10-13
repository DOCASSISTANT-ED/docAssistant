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
        3. Sorunun cevabı kaynaklarda yoksa yalnızca şu cümleyi yaz, başka hiçbir şey ekleme: {NotFoundAnswer}
        4. Türkçe, kısa ve doğrudan cevap ver.
        """.ReplaceLineEndings("\n");

    // sources: in the order search returned them; the first becomes [1].
    // history: earlier messages of the same conversation, oldest first.
    public static IList<ChatMessage> Build(
        IReadOnlyList<SearchResult> sources,
        IReadOnlyList<Message> history,
        string question)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, Instructions) };

        foreach (var message in history)
        {
            messages.Add(message.Role == MessageRole.User
                ? new ChatMessage(ChatRole.User, message.Content)
                // An earlier answer's numbers pointed at that question's sources. Here
                // they would look like references to the new ones, so they are removed.
                : new ChatMessage(ChatRole.Assistant, SourceNumber().Replace(message.Content, string.Empty).Trim()));
        }

        var text = new StringBuilder("Kaynaklar:\n\n");

        for (var i = 0; i < sources.Count; i++)
        {
            // The chunk's content starts with its context header: the document's title
            // and section path (decisions #27).
            text.Append('[').Append(i + 1).Append("]\n").Append(sources[i].Content).Append("\n\n");
        }

        text.Append("Soru: ").Append(question);
        messages.Add(new ChatMessage(ChatRole.User, text.ToString()));

        return messages;
    }

    [GeneratedRegex(@"\s*\[\d+\]")]
    private static partial Regex SourceNumber();
}
