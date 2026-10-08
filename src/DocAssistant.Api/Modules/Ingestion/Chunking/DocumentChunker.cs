using System.Globalization;
using System.Text;
using DocAssistant.Api.Modules.Ingestion.Parsing;

namespace DocAssistant.Api.Modules.Ingestion.Chunking;

// Turns a parsed document into chunks (docs/decisions.md #43). The one chunking algorithm
// for every file format: it sees only blocks, never the file (#21).
//
// Headings open a section and close the chunk being built, so a chunk never spans two
// sections. Inside a section, paragraphs and table rows are added one by one until the
// next one would push the text over MaxTextLength. A paragraph or row that is too long on
// its own is split into sentences, and a sentence that is still too long into words.
public static class DocumentChunker
{
    // Characters of text per chunk, not counting the context header.
    public const int MaxTextLength = 1500;

    public static IReadOnlyList<ChunkDraft> Chunk(string documentTitle, ParsedDocument document)
    {
        var builder = new ChunkBuilder(documentTitle);

        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    builder.StartSection(heading.Text, heading.Level);
                    break;

                case ParagraphBlock paragraph:
                    builder.AddText(paragraph.Text, "\n\n", paragraph.PageNumber);
                    break;

                case TableBlock table:
                    var separator = "\n\n";
                    foreach (var row in FormatRows(table))
                    {
                        builder.AddText(row, separator, table.PageNumber);
                        separator = "\n";
                    }

                    break;
            }
        }

        builder.Flush();

        return builder.Chunks;
    }

    // One line per data row, "Sütun: değer; Sütun: değer", so each row can be understood
    // without the header row. Empty cells are left out.
    private static IEnumerable<string> FormatRows(TableBlock table)
    {
        var header = table.HasHeaderRow && table.Rows.Count > 0 ? table.Rows[0] : null;
        var dataRows = header is null ? table.Rows : table.Rows.Skip(1);

        foreach (var row in dataRows)
        {
            var cells = new List<string>();

            for (var column = 0; column < row.Count; column++)
            {
                var value = row[column].Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                var name = header is not null && column < header.Count ? header[column].Trim() : "";
                cells.Add(name.Length > 0 ? $"{name}: {value}" : value);
            }

            if (cells.Count > 0)
            {
                yield return string.Join("; ", cells);
            }
        }
    }

    // Pieces of at most MaxTextLength characters: the text itself when it fits, otherwise
    // its sentences, otherwise words, and as a last resort fixed-length slices.
    private static IEnumerable<string> SplitToFit(string text)
    {
        if (text.Length <= MaxTextLength)
        {
            yield return text;
            yield break;
        }

        foreach (var sentence in TurkishSentenceSplitter.Split(text))
        {
            if (sentence.Length <= MaxTextLength)
            {
                yield return sentence;
                continue;
            }

            foreach (var piece in SplitWords(sentence))
            {
                yield return piece;
            }
        }
    }

    private static IEnumerable<string> SplitWords(string sentence)
    {
        var piece = new StringBuilder();

        foreach (var word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (piece.Length > 0 && piece.Length + 1 + word.Length > MaxTextLength)
            {
                yield return piece.ToString();
                piece.Clear();
            }

            // A "word" longer than the limit (a long URL, a run of symbols) is cut up.
            var rest = word;
            while (rest.Length > MaxTextLength)
            {
                if (piece.Length > 0)
                {
                    yield return piece.ToString();
                    piece.Clear();
                }

                yield return rest[..MaxTextLength];
                rest = rest[MaxTextLength..];
            }

            if (piece.Length > 0)
            {
                piece.Append(' ');
            }

            piece.Append(rest);
        }

        if (piece.Length > 0)
        {
            yield return piece.ToString();
        }
    }

    private sealed class ChunkBuilder(string documentTitle)
    {
        private static readonly StringComparer TitleComparer =
            StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);

        private readonly List<(int Level, string Text)> _headings = [];
        private readonly StringBuilder _text = new();
        private int? _pageNumber;

        public List<ChunkDraft> Chunks { get; } = [];

        // Null when no heading is open (decisions #27: the header then has only the title).
        private string? SectionPath =>
            _headings.Count == 0 ? null : string.Join(" > ", _headings.Select(h => h.Text));

        public void StartSection(string text, int level)
        {
            Flush();

            // A heading closes every open heading at its own level or deeper.
            while (_headings.Count > 0 && _headings[^1].Level >= level)
            {
                _headings.RemoveAt(_headings.Count - 1);
            }

            // A document often opens with its own name as a heading; repeating it after
            // "Belge: <title>" in every header adds nothing.
            var trimmed = text.Trim();
            if (trimmed.Length > 0 && !TitleComparer.Equals(trimmed, documentTitle.Trim()))
            {
                _headings.Add((level, trimmed));
            }
        }

        // separator goes before the text unless it opens a chunk; the pieces of a text
        // that had to be split are joined with spaces.
        public void AddText(string text, string separator, int? pageNumber)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return;
            }

            foreach (var piece in SplitToFit(trimmed))
            {
                Add(piece, separator, pageNumber);
                separator = " ";
            }
        }

        public void Flush()
        {
            if (_text.Length == 0)
            {
                return;
            }

            var sectionPath = SectionPath;
            var header = sectionPath is null
                ? $"Belge: {documentTitle}"
                : $"Belge: {documentTitle} > {sectionPath}";

            Chunks.Add(new ChunkDraft(Chunks.Count, $"{header}\n\n{_text}", _pageNumber, sectionPath));

            _text.Clear();
            _pageNumber = null;
        }

        private void Add(string piece, string separator, int? pageNumber)
        {
            if (_text.Length > 0 && _text.Length + separator.Length + piece.Length > MaxTextLength)
            {
                Flush();
            }

            if (_text.Length == 0)
            {
                // decisions #43: a chunk's page is the page of its first block.
                _pageNumber = pageNumber;
            }
            else
            {
                _text.Append(separator);
            }

            _text.Append(piece);
        }
    }
}
