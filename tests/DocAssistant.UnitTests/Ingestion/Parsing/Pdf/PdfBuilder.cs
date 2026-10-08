using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DocAssistant.UnitTests.Ingestion.Parsing.Pdf;

// Builds small PDF files in memory with PdfPig's writer, so each test controls exactly
// what the parser works from: font size and the vertical position of every line.
//
// Text is written top-down. Each line's baseline sits LineSpacing × its font size below
// the previous one; Gap adds extra space, as between two paragraphs. The built-in fonts
// cover ASCII only, so Turkish characters are tested with the shared sample PDF instead.
internal sealed class PdfBuilder
{
    // Distance between baselines, as a share of the font size: Word's "single" spacing.
    public const double LineSpacing = 1.2;

    private const double TopBaseline = 800;
    private const double LeftMargin = 50;

    private readonly List<List<(string Text, double FontSize, double Baseline)>> _pages = [[]];
    private double? _lastBaseline;
    private double _pendingGap;

    // One line of text.
    public PdfBuilder Line(string text, double fontSize = 12)
    {
        var baseline = _lastBaseline is { } last
            ? last - fontSize * LineSpacing - _pendingGap
            : TopBaseline;

        _pages[^1].Add((text, fontSize, baseline));
        _lastBaseline = baseline;
        _pendingGap = 0;

        return this;
    }

    // Lines with normal spacing, followed by a paragraph gap.
    public PdfBuilder Paragraph(params string[] lines) => Paragraph(12, lines);

    public PdfBuilder Paragraph(double fontSize, params string[] lines)
    {
        foreach (var line in lines)
        {
            Line(line, fontSize);
        }

        return Gap(fontSize);
    }

    // Extra space before the next line, in points.
    public PdfBuilder Gap(double points)
    {
        _pendingGap += points;
        return this;
    }

    // Starts a new page; the next line goes to its top.
    public PdfBuilder Page()
    {
        _pages.Add([]);
        _lastBaseline = null;
        _pendingGap = 0;

        return this;
    }

    public MemoryStream Build()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var lines in _pages)
        {
            var page = builder.AddPage(PageSize.A4);

            foreach (var (text, fontSize, baseline) in lines)
            {
                page.AddText(text, fontSize, new PdfPoint(LeftMargin, baseline), font);
            }
        }

        return new MemoryStream(builder.Build());
    }
}
