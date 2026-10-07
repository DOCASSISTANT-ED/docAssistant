using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.Api.Modules.Ingestion.Parsing.Docx;

namespace DocAssistant.UnitTests.Ingestion.Parsing.Docx;

// The shared sample document, a real file saved by Word. Its expected structure is the
// table in tests/TestData/README.md.
public class DocxSampleDocumentTests
{
    private static readonly ParsedDocument Sample = ParseSample();

    [Fact]
    public void ReturnsEveryBlockInReadingOrder()
    {
        string[] expected =
        [
            "Heading", "Paragraph", "Paragraph",
            "Heading", "Paragraph", "Paragraph",
            "Heading", "Paragraph",
            "Heading", "Paragraph", "Table", "Paragraph",
            "Heading", "Paragraph", "Paragraph",
        ];

        var actual = Sample.Blocks.Select(block => block.GetType().Name.Replace("Block", string.Empty));

        Assert.Equal(expected, actual);
    }

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

    // In the PDF this paragraph is five lines; in the DOCX it is stored as one piece.
    [Fact]
    public void KeepsALongParagraphAsOneBlock()
    {
        var paragraph = Paragraphs().Single(text => text.StartsWith("Haftalık çalışma süresi"));

        Assert.EndsWith("yöneticilerine iletir.", paragraph);
        Assert.Contains("09:00 ile 18:00", paragraph);
    }

    // Chunking must later split sentences without breaking on these (PLANNING: "Dr.",
    // "vb.", "md."), so the parser has to deliver them untouched.
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

    [Fact]
    public void ReadsTheTableWithItsHeaderRow()
    {
        var table = Assert.Single(Sample.Blocks.OfType<TableBlock>());

        Assert.True(table.HasHeaderRow);
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(["Kıdem", "Yıllık izin günü"], table.Rows[0]);
        Assert.Equal(["1–5 yıl", "14"], table.Rows[1]);
        Assert.Equal(["5–15 yıl", "20"], table.Rows[2]);
        Assert.Equal(["15 yıl ve üzeri", "26"], table.Rows[3]);
    }

    // decisions #22: a DOCX file stores no page numbers.
    [Fact]
    public void LeavesPageNumbersEmpty()
    {
        Assert.All(Sample.Blocks, block => Assert.Null(block.PageNumber));
    }

    private static IEnumerable<string> Paragraphs() => Sample.Blocks.OfType<ParagraphBlock>().Select(p => p.Text);

    private static ParsedDocument ParseSample()
    {
        using var file = TestData.Open(TestData.SampleDocx);

        return new DocxDocumentParser().Parse(file);
    }
}
