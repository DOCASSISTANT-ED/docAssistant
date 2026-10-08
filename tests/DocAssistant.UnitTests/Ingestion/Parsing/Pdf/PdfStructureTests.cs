using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.Api.Modules.Ingestion.Parsing.Pdf;

namespace DocAssistant.UnitTests.Ingestion.Parsing.Pdf;

// How the parser rebuilds paragraphs and headings from letter positions and font sizes
// (docs/decisions.md #23, #25). Each test builds a small PDF with exactly the layout it
// is about.
public class PdfStructureTests
{
    private const string LongLine = "The employee informs the manager about the leave request in writing";

    // ---- Lines and paragraphs ---------------------------------------------------------

    [Fact]
    public void WordsOfALineAreKeptInOrder()
    {
        var blocks = Parse(new PdfBuilder().Paragraph("Annual leave is fourteen days"));

        Assert.Equal("Annual leave is fourteen days", Assert.IsType<ParagraphBlock>(Assert.Single(blocks)).Text);
    }

    [Fact]
    public void LinesWithNormalSpacingFormOneParagraph()
    {
        var blocks = Parse(new PdfBuilder().Paragraph("First line of the", "paragraph goes on", "and ends here."));

        Assert.Equal(
            "First line of the paragraph goes on and ends here.",
            Assert.IsType<ParagraphBlock>(Assert.Single(blocks)).Text);
    }

    [Fact]
    public void LargerGapBetweenLinesStartsANewParagraph()
    {
        var blocks = Parse(new PdfBuilder()
            .Paragraph(LongLine, "first paragraph ends here.")
            .Paragraph(LongLine, "second paragraph ends here."));

        Assert.Equal(
            [$"{LongLine} first paragraph ends here.", $"{LongLine} second paragraph ends here."],
            Texts(blocks));
    }

    // Line spacing is measured per document, so double-spaced text still forms paragraphs.
    [Fact]
    public void DoubleSpacedLinesStillFormOneParagraph()
    {
        var blocks = Parse(new PdfBuilder()
            .Line(LongLine).Gap(12)
            .Line("and the second line").Gap(12)
            .Line("and the third line."));

        Assert.Equal($"{LongLine} and the second line and the third line.", Assert.Single(Texts(blocks)));
    }

    // Each block carries one page number, so a paragraph broken by a page stays in two.
    [Fact]
    public void ParagraphIsNotJoinedAcrossPages()
    {
        var blocks = Parse(new PdfBuilder()
            .Paragraph(LongLine, "continues on the")
            .Page()
            .Paragraph("next page.", LongLine));

        Assert.Equal(2, blocks.Count);
        Assert.Equal([1, 2], blocks.Select(block => block.PageNumber));
    }

    [Fact]
    public void BlocksCarryTheirPageNumber()
    {
        var blocks = Parse(new PdfBuilder()
            .Paragraph(LongLine, "first page.")
            .Page()
            .Paragraph(LongLine, "second page.")
            .Page()
            .Paragraph(LongLine, "third page."));

        Assert.Equal([1, 2, 3], blocks.Select(block => block.PageNumber));
    }

    // ---- Words broken at the end of a line (decisions #25) ----------------------------

    [Fact]
    public void WordHyphenatedAtTheEndOfALineIsJoined()
    {
        var blocks = Parse(new PdfBuilder().Paragraph("This regulation is valid for every employ-", "ee of the company."));

        Assert.Equal("This regulation is valid for every employee of the company.", Assert.Single(Texts(blocks)));
    }

    // A hyphen before a capital letter is part of a compound name, not a line-end break.
    [Fact]
    public void HyphenBeforeACapitalLetterIsKept()
    {
        var blocks = Parse(new PdfBuilder().Paragraph("Trips on the route Ankara-", "Istanbul are paid daily."));

        Assert.Contains("Ankara-", Assert.Single(Texts(blocks)));
    }

    // An en dash is punctuation ("1–5 years"), never a hyphenation mark.
    [Fact]
    public void DashAtTheEndOfALineIsKept()
    {
        var blocks = Parse(new PdfBuilder().Paragraph("Employees with 1–", "5 years of service get 14 days."));

        Assert.Contains("1–", Assert.Single(Texts(blocks)));
    }

    // ---- Headings, guessed from font size (decisions #23) -----------------------------

    [Fact]
    public void LargerShortTextIsAHeading()
    {
        var blocks = Parse(new PdfBuilder()
            .Paragraph(14, "Annual Leave")
            .Paragraph(LongLine, "and the paragraph ends here."));

        var heading = Assert.IsType<HeadingBlock>(blocks[0]);
        Assert.Equal("Annual Leave", heading.Text);
        Assert.Equal(1, heading.Level);
        Assert.IsType<ParagraphBlock>(blocks[1]);
    }

    // 12.5 pt over a 12 pt body is less than the 1.1 factor: a styling detail, not a heading.
    [Fact]
    public void SlightlyLargerTextIsNotAHeading()
    {
        var blocks = Parse(new PdfBuilder()
            .Paragraph(12.5, "Annual Leave")
            .Paragraph(LongLine, "and the paragraph ends here."));

        Assert.All(blocks, block => Assert.IsType<ParagraphBlock>(block));
    }

    // Large print that runs long is a pull quote or a cover text, not a heading.
    [Fact]
    public void LongLargeTextIsNotAHeading()
    {
        var blocks = Parse(new PdfBuilder()
            .Paragraph(14, LongLine, LongLine, LongLine, "end.")
            .Paragraph(LongLine, LongLine, LongLine, LongLine, LongLine, "end."));

        Assert.All(blocks, block => Assert.IsType<ParagraphBlock>(block));
    }

    [Fact]
    public void LargerFontsAreHigherHeadingLevels()
    {
        var blocks = Parse(new PdfBuilder()
            .Paragraph(20, "Regulation")
            .Paragraph(16, "Part 1")
            .Paragraph(14, "Section 1.1")
            .Paragraph(LongLine, "and the paragraph ends here.")
            .Paragraph(16, "Part 2")
            .Paragraph(LongLine, "and the paragraph ends here."));

        (string Text, int Level)[] expected = [("Regulation", 1), ("Part 1", 2), ("Section 1.1", 3), ("Part 2", 2)];

        Assert.Equal(expected, blocks.OfType<HeadingBlock>().Select(heading => (heading.Text, heading.Level)));
    }

    // The body is the size most of the text is written in, not the smallest one: small
    // print such as a footnote must not turn the body text into headings.
    [Fact]
    public void BodyIsTheSizeOfMostOfTheText()
    {
        var blocks = Parse(new PdfBuilder()
            .Paragraph(LongLine, LongLine, "and the paragraph ends here.")
            .Paragraph(9, "1 Footnote."));

        Assert.All(blocks, block => Assert.IsType<ParagraphBlock>(block));
    }

    // The normal line spacing is the smallest gap between two lines of the same size. When
    // no paragraph has a second line, that smallest gap is the space between paragraphs;
    // separate one-line paragraphs (a list, short clauses) must still not be glued into one.
    [Fact]
    public void OneLineParagraphsStaySeparate()
    {
        var blocks = Parse(new PdfBuilder()
            .Paragraph("Item one.")
            .Paragraph("Item two.")
            .Paragraph("Item three."));

        Assert.Equal(["Item one.", "Item two.", "Item three."], Texts(blocks));
    }

    private static IReadOnlyList<DocumentBlock> Parse(PdfBuilder builder)
    {
        using var file = builder.Build();

        return new PdfDocumentParser().Parse(file).Blocks;
    }

    private static List<string> Texts(IEnumerable<DocumentBlock> blocks) =>
        blocks.Select(block => block switch
        {
            ParagraphBlock paragraph => paragraph.Text,
            HeadingBlock heading => heading.Text,
            _ => throw new InvalidOperationException($"Unexpected {block.GetType().Name}."),
        }).ToList();
}
