using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocAssistant.UnitTests.Ingestion.Parsing.Docx;

// Builds small DOCX files in memory, so each test can show exactly the structure it is
// about instead of depending on a prepared file.
internal sealed class DocxBuilder
{
    private readonly List<OpenXmlElement> _body = [];
    private readonly List<Style> _styles = [];

    public DocxBuilder Paragraph(string text, string? styleId = null, int? outlineLevel = null)
    {
        var paragraph = new Paragraph();

        if (styleId is not null || outlineLevel is not null)
        {
            var properties = new ParagraphProperties();

            if (styleId is not null)
            {
                properties.Append(new ParagraphStyleId { Val = styleId });
            }

            if (outlineLevel is not null)
            {
                properties.Append(new OutlineLevel { Val = outlineLevel.Value });
            }

            paragraph.Append(properties);
        }

        paragraph.Append(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        _body.Add(paragraph);

        return this;
    }

    // Any body element, for the cases the helpers above do not cover.
    public DocxBuilder Element(OpenXmlElement element)
    {
        _body.Add(element);
        return this;
    }

    // A paragraph style. outlineLevel is Word's own number: 0 is "Heading 1".
    public DocxBuilder Style(string styleId, int? outlineLevel = null, string? basedOn = null)
    {
        var style = new Style { Type = StyleValues.Paragraph, StyleId = styleId };

        if (basedOn is not null)
        {
            style.Append(new BasedOn { Val = basedOn });
        }

        if (outlineLevel is not null)
        {
            style.Append(new StyleParagraphProperties(new OutlineLevel { Val = outlineLevel.Value }));
        }

        _styles.Add(style);

        return this;
    }

    // Each row is its cells; each cell is its paragraphs.
    public DocxBuilder Table(bool firstRowIsHeader, params string[][][] rows)
    {
        var table = new Table();

        for (var i = 0; i < rows.Length; i++)
        {
            var row = new TableRow();

            if (i == 0 && firstRowIsHeader)
            {
                row.Append(new TableRowProperties(new TableHeader()));
            }

            foreach (var cell in rows[i])
            {
                row.Append(new TableCell(cell.Select(text => new Paragraph(new Run(new Text(text))))));
            }

            table.Append(row);
        }

        _body.Add(table);

        return this;
    }

    public MemoryStream Build()
    {
        var stream = new MemoryStream();

        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(_body));

            if (_styles.Count > 0)
            {
                main.AddNewPart<StyleDefinitionsPart>().Styles = new Styles(_styles);
            }
        }

        stream.Position = 0;

        return stream;
    }

    // One table cell with a single paragraph, for the common case.
    public static string[] Cell(string text) => [text];
}
