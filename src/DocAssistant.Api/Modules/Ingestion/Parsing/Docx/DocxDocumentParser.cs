using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocAssistant.Api.Modules.Ingestion.Parsing.Docx;

// A DOCX file is a zip of XML parts. Unlike a PDF it stores structure, not just looks:
// paragraphs, their styles and tables are marked explicitly (docs/decisions.md #21).
// It stores no page numbers, so every block's PageNumber is null (decisions #22).
public sealed partial class DocxDocumentParser : IDocumentParser
{
    // Shown to the user and stored in documents.failure_reason (docs/decisions.md #24),
    // so they are written in the product's language.
    public const string UnreadableFileMessage =
        "Dosya okunamadı. Dosya bozuk olabilir ya da geçerli bir Word (DOCX) belgesi değil.";

    public const string PasswordProtectedOrLegacyMessage =
        "Belge parola korumalı ya da eski Word biçiminde (.doc). Parolayı kaldırıp DOCX olarak kaydederek yeniden yükleyin.";

    public const string NoTextMessage = "Belgede okunabilir metin bulunamadı.";

    // A DOCX is a zip. Password-protected Word files, and legacy .doc files, are instead
    // "compound files" that start with these bytes; the DOCX reader would only report
    // them as corrupt, so they get their own, clearer message.
    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    // How far a style's "based on" chain is followed when looking for a heading level.
    private const int MaxStyleChainLength = 20;

    public DocumentSourceType SourceType => DocumentSourceType.Docx;

    public ParsedDocument Parse(Stream content)
    {
        // The DOCX reader needs to jump around in the file. The caller's stream (e.g. from
        // file storage) may not allow that, so work on an in-memory copy; uploads are at
        // most 20 MB (decisions #32).
        using var file = new MemoryStream();
        content.CopyTo(file);
        file.Position = 0;

        if (StartsWith(file, CompoundFileSignature))
        {
            throw new DocumentParseException(PasswordProtectedOrLegacyMessage);
        }

        try
        {
            return ParseCore(file);
        }
        catch (Exception ex) when (ex is not DocumentParseException)
        {
            // The OpenXML SDK reports broken files with several exception types; none of
            // their messages are meant for end users.
            throw new DocumentParseException(UnreadableFileMessage, ex);
        }
    }

    private static ParsedDocument ParseCore(Stream file)
    {
        using var document = WordprocessingDocument.Open(file, isEditable: false);

        var body = document.MainDocumentPart?.Document?.Body
            ?? throw new DocumentParseException(UnreadableFileMessage);

        var styles = ParagraphStylesById(document.MainDocumentPart.StyleDefinitionsPart);
        var blocks = ReadBlocks(body, styles);

        // Failing loudly beats reporting "done, 0 chunks" (decisions #23, #24).
        if (blocks.Count == 0)
        {
            throw new DocumentParseException(NoTextMessage);
        }

        return new ParsedDocument(blocks);
    }

    private static List<DocumentBlock> ReadBlocks(Body body, IReadOnlyDictionary<string, Style> styles)
    {
        var blocks = new List<DocumentBlock>();

        // Paragraphs and tables are siblings in the body; walking them together keeps the
        // reading order.
        foreach (var element in Unwrapped(body))
        {
            switch (element)
            {
                case Paragraph paragraph when TextOf(paragraph) is { Length: > 0 } text:
                    blocks.Add(HeadingLevel(paragraph, styles) is { } level
                        ? new HeadingBlock(text, level, PageNumber: null)
                        : new ParagraphBlock(text, PageNumber: null));
                    break;

                case Table table when ReadTable(table) is { } tableBlock:
                    blocks.Add(tableBlock);
                    break;
            }
        }

        return blocks;
    }

    private static TableBlock? ReadTable(Table table)
    {
        // Rows with no text at all are left out.
        var rows = Unwrapped(table).OfType<TableRow>()
            .Select(row => (Row: row, Cells: Unwrapped(row).OfType<TableCell>().Select(TextOf).ToList()))
            .Where(row => row.Cells.Any(cell => cell.Length > 0))
            .ToList();

        if (rows.Count == 0)
        {
            return null;
        }

        // Word's "Repeat as header row" marks the rows that hold the column names. Only an
        // explicit mark counts: a bold first row is a guess we leave to the reader.
        var hasHeaderRow = rows[0].Row.TableRowProperties?.GetFirstChild<TableHeader>() is not null;

        return new TableBlock(
            rows.Select(row => (IReadOnlyList<string>)row.Cells).ToList(),
            hasHeaderRow,
            PageNumber: null);
    }

    // A cell can hold several paragraphs; they are joined with a space.
    private static string TextOf(TableCell cell) =>
        string.Join(" ", Unwrapped(cell).OfType<Paragraph>().Select(TextOf).Where(text => text.Length > 0));

    // The children of container, with content controls (w:sdt) opened up. Templates, forms,
    // cover pages and tables of contents wrap ordinary paragraphs, tables, rows or cells in
    // them; reading only direct children would silently drop that text. Content controls
    // can be nested, so their content is unwrapped the same way.
    private static IEnumerable<OpenXmlElement> Unwrapped(OpenXmlElement container)
    {
        foreach (var child in container.ChildElements)
        {
            if (child is not SdtElement contentControl)
            {
                yield return child;
                continue;
            }

            var content = contentControl.ChildElements
                .FirstOrDefault(e => e is SdtContentBlock or SdtContentRow or SdtContentCell);

            if (content is null)
            {
                continue;
            }

            foreach (var inner in Unwrapped(content))
            {
                yield return inner;
            }
        }
    }

    // Headings are found by outline level, the number Word itself uses for the navigation
    // pane, not by style name: Word localizes style ids (a Turkish Word may save
    // "Heading 2" as "Balk2"), but the outline level is the same in every language.
    private static int? HeadingLevel(Paragraph paragraph, IReadOnlyDictionary<string, Style> styles)
    {
        var properties = paragraph.ParagraphProperties;

        // An outline level set on the paragraph itself overrides its style.
        if (properties?.OutlineLevel?.Val?.Value is { } direct)
        {
            return ToHeadingLevel(direct);
        }

        // Otherwise follow the style and the styles it is based on. The step limit guards
        // against a malformed file whose styles are based on each other in a loop.
        var styleId = properties?.ParagraphStyleId?.Val?.Value;
        for (var step = 0; styleId is not null && step < MaxStyleChainLength; step++)
        {
            if (!styles.TryGetValue(styleId, out var style))
            {
                break;
            }

            if (style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is { } inherited)
            {
                return ToHeadingLevel(inherited);
            }

            styleId = style.BasedOn?.Val?.Value;
        }

        return null;
    }

    // Word's outline levels 0–8 are heading levels 1–9; 9 means "body text".
    private static int? ToHeadingLevel(int outlineLevel) =>
        outlineLevel is >= 0 and <= 8 ? outlineLevel + 1 : null;

    private static Dictionary<string, Style> ParagraphStylesById(StyleDefinitionsPart? part)
    {
        var styles = new Dictionary<string, Style>();

        foreach (var style in part?.Styles?.Elements<Style>() ?? [])
        {
            if (style.Type?.Value == StyleValues.Paragraph && style.StyleId?.Value is { } id)
            {
                styles.TryAdd(id, style);
            }
        }

        return styles;
    }

    // Only the visible text runs (w:t): field codes such as PAGE and text deleted under
    // track changes are separate elements and are left out. Line breaks (w:br, w:cr) and
    // tabs (w:tab) are elements too, not characters inside w:t; they become spaces so the
    // words around them stay apart. Runs of whitespace are then collapsed to one space.
    private static string TextOf(Paragraph paragraph)
    {
        var text = new StringBuilder();

        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case Text run:
                    text.Append(run.Text);
                    break;

                case Break or CarriageReturn or TabChar:
                    text.Append(' ');
                    break;
            }
        }

        return Whitespace().Replace(text.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static bool StartsWith(Stream stream, byte[] signature)
    {
        Span<byte> start = stackalloc byte[signature.Length];
        var read = stream.Read(start);
        stream.Position = 0;

        return read == signature.Length && start.SequenceEqual(signature);
    }
}
