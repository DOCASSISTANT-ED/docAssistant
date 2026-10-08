using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.Api.Modules.Ingestion.Parsing.Docx;
using DocAssistant.Api.Modules.Ingestion.Parsing.Pdf;

namespace DocAssistant.UnitTests.Ingestion.Parsing.Pdf;

// The shared sample document, a real file saved by Word as PDF. Its expected structure is
// the table in tests/TestData/README.md; the DOCX version of the same document is tested
// in DocxSampleDocumentTests with the same expectations where the formats agree.
public class PdfSampleDocumentTests
{
    private static readonly ParsedDocument Sample = ParseSample();

    // The four table rows: a PDF does not mark tables, so in phase 2 they arrive as
    // ordinary paragraphs, one per row (decisions #23).
    private static readonly string[] TableRows =
        ["Kıdem Yıllık izin günü", "1–5 yıl 14", "5–15 yıl 20", "15 yıl ve üzeri 26"];

    [Fact]
    public void ReturnsEveryBlockInReadingOrder()
    {
        string[] expected =
        [
            "Heading", "Paragraph", "Paragraph",
            "Heading", "Paragraph", "Paragraph",
            "Heading", "Paragraph",
            "Heading", "Paragraph", "Paragraph", "Paragraph", "Paragraph", "Paragraph", "Paragraph",
            "Heading", "Paragraph", "Paragraph",
        ];

        var actual = Sample.Blocks.Select(block => block.GetType().Name.Replace("Block", string.Empty));

        Assert.Equal(expected, actual);
    }

    // Guessed from font size: 20, 16 and 14 pt over a 12 pt body (README "Biçim").
    [Fact]
    public void FindsHeadingsWithTheirLevels()
    {
        (string Text, int Level)[] expected =
        [
            ("İK Yönetmeliği", 1),
            ("Bölüm 1: Çalışma Saatleri", 2),
            ("1.1 Fazla Mesai", 3),
            ("Bölüm 2: Yıllık İzin", 2),
            ("Bölüm 3: Harcırah", 2),
        ];

        var actual = Sample.Blocks.OfType<HeadingBlock>().Select(heading => (heading.Text, heading.Level));

        Assert.Equal(expected, actual);
    }

    // decisions #22: a PDF has pages. "Bölüm 3" starts after a page break.
    [Fact]
    public void GivesEveryBlockItsPageNumber()
    {
        var pageTwoStart = Sample.Blocks
            .Select((block, index) => (block, index))
            .Single(pair => pair.block is HeadingBlock { Text: "Bölüm 3: Harcırah" })
            .index;

        Assert.All(Sample.Blocks.Take(pageTwoStart), block => Assert.Equal(1, block.PageNumber));
        Assert.All(Sample.Blocks.Skip(pageTwoStart), block => Assert.Equal(2, block.PageNumber));
    }

    // Five lines in the PDF, one paragraph in the document (decisions #25).
    [Fact]
    public void JoinsTheLinesOfAParagraph()
    {
        var paragraph = Paragraphs().Single(text => text.StartsWith("Haftalık çalışma süresi"));

        Assert.EndsWith("yöneticilerine iletir.", paragraph);
        Assert.Contains("09:00 ile 18:00", paragraph);
        Assert.DoesNotContain("  ", paragraph);
    }

    // Chunking must later split sentences without breaking on these, so the parser has
    // to deliver them untouched. The same cases as DocxSampleDocumentTests.
    [Theory]
    [InlineData("Örnek Lojistik A.Ş.")]
    [InlineData("şube vb. bütün birimlerde")]
    [InlineData("İnsan Kaynakları Md. sorumludur")]
    [InlineData("Dr. raporunda")]
    [InlineData("günlük 1.500 TL harcırah")]
    public void KeepsTurkishTextAndAbbreviationsIntact(string expectedText)
    {
        Assert.Contains(Paragraphs(), text => text.Contains(expectedText));
    }

    // decisions #23: tables are not detected in phase 2; their text is kept as paragraphs.
    [Fact]
    public void ReadsTheTableAsPlainText()
    {
        Assert.Empty(Sample.Blocks.OfType<TableBlock>());

        foreach (var row in TableRows)
        {
            Assert.Contains(row, Paragraphs());
        }
    }

    // decisions #27: the two versions of a document should give the same text, so search
    // results do not depend on the format a customer happened to upload. The table is
    // the one planned difference.
    [Fact]
    public void ParagraphsMatchTheDocxVersionOfTheSameDocument()
    {
        using var docxFile = TestData.Open(TestData.SampleDocx);
        var docxParagraphs = new DocxDocumentParser().Parse(docxFile).Blocks
            .OfType<ParagraphBlock>()
            .Select(paragraph => paragraph.Text);

        var pdfParagraphs = Paragraphs().Where(text => !TableRows.Contains(text));

        Assert.Equal(docxParagraphs, pdfParagraphs);
    }

    private static IEnumerable<string> Paragraphs() => Sample.Blocks.OfType<ParagraphBlock>().Select(p => p.Text);

    private static ParsedDocument ParseSample()
    {
        using var file = TestData.Open(TestData.SamplePdf);

        return new PdfDocumentParser().Parse(file);
    }
}
