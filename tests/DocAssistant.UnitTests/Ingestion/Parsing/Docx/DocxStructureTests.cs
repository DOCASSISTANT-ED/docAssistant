using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.Api.Modules.Ingestion.Parsing.Docx;
using DocumentFormat.OpenXml.Wordprocessing;
using static DocAssistant.UnitTests.Ingestion.Parsing.Docx.DocxBuilder;

namespace DocAssistant.UnitTests.Ingestion.Parsing.Docx;

// How the parser turns Word's structure into blocks, one rule per test, on documents
// built for the purpose.
public class DocxStructureTests
{
    [Fact]
    public void SourceTypeIsDocx()
    {
        Assert.Equal(DocumentSourceType.Docx, new DocxDocumentParser().SourceType);
    }

    // Word's outline level 0 is "Heading 1".
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(8, 9)]
    public void OutlineLevelOnTheParagraphMakesItAHeading(int outlineLevel, int expectedHeadingLevel)
    {
        var blocks = Parse(new DocxBuilder().Paragraph("Başlık", outlineLevel: outlineLevel));

        var heading = Assert.IsType<HeadingBlock>(Assert.Single(blocks));
        Assert.Equal("Başlık", heading.Text);
        Assert.Equal(expectedHeadingLevel, heading.Level);
    }

    // A Turkish Word saves "Heading 2" under the style id "Balk2". The parser must not
    // depend on the English name.
    [Fact]
    public void HeadingIsFoundThroughALocalizedStyle()
    {
        var blocks = Parse(new DocxBuilder()
            .Style("Balk2", outlineLevel: 1)
            .Paragraph("Bölüm 1", styleId: "Balk2"));

        var heading = Assert.IsType<HeadingBlock>(Assert.Single(blocks));
        Assert.Equal(2, heading.Level);
    }

    // A custom style based on a heading style is still a heading.
    [Fact]
    public void HeadingLevelIsInheritedFromTheStyleItIsBasedOn()
    {
        var blocks = Parse(new DocxBuilder()
            .Style("Heading1", outlineLevel: 0)
            .Style("CompanyTitle", basedOn: "Heading1")
            .Paragraph("Şirket Başlığı", styleId: "CompanyTitle"));

        var heading = Assert.IsType<HeadingBlock>(Assert.Single(blocks));
        Assert.Equal(1, heading.Level);
    }

    [Fact]
    public void OutlineLevelOnTheParagraphOverridesItsStyle()
    {
        var blocks = Parse(new DocxBuilder()
            .Style("Heading1", outlineLevel: 0)
            .Paragraph("Alt başlık", styleId: "Heading1", outlineLevel: 2));

        var heading = Assert.IsType<HeadingBlock>(Assert.Single(blocks));
        Assert.Equal(3, heading.Level);
    }

    // Outline level 9 is Word's value for "body text".
    [Fact]
    public void BodyTextOutlineLevelIsNotAHeading()
    {
        var blocks = Parse(new DocxBuilder().Paragraph("Düz metin", outlineLevel: 9));

        Assert.IsType<ParagraphBlock>(Assert.Single(blocks));
    }

    // The name alone does not make a heading: only the outline level counts.
    [Fact]
    public void StyleWithoutAnOutlineLevelIsNotAHeading()
    {
        var blocks = Parse(new DocxBuilder()
            .Style("Heading1")
            .Paragraph("Yalnızca adı başlık", styleId: "Heading1"));

        Assert.IsType<ParagraphBlock>(Assert.Single(blocks));
    }

    [Fact]
    public void ParagraphWithAnUnknownStyleIsAParagraph()
    {
        var blocks = Parse(new DocxBuilder().Paragraph("Metin", styleId: "DoesNotExist"));

        Assert.IsType<ParagraphBlock>(Assert.Single(blocks));
    }

    // A malformed file could make styles point at each other; the parser must not hang.
    [Fact]
    public void StylesBasedOnEachOtherInALoopDoNotHangTheParser()
    {
        var blocks = Parse(new DocxBuilder()
            .Style("A", basedOn: "B")
            .Style("B", basedOn: "A")
            .Paragraph("Metin", styleId: "A"));

        Assert.IsType<ParagraphBlock>(Assert.Single(blocks));
    }

    [Fact]
    public void EmptyAndWhitespaceOnlyParagraphsAreSkipped()
    {
        var blocks = Parse(new DocxBuilder()
            .Paragraph("Birinci")
            .Paragraph(string.Empty)
            .Paragraph("   ")
            .Paragraph("İkinci"));

        Assert.Equal(["Birinci", "İkinci"], blocks.Cast<ParagraphBlock>().Select(p => p.Text));
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmed()
    {
        var blocks = Parse(new DocxBuilder().Paragraph("  Metin  "));

        Assert.Equal("Metin", Assert.IsType<ParagraphBlock>(Assert.Single(blocks)).Text);
    }

    // Word splits a paragraph into runs wherever the formatting changes.
    [Fact]
    public void TextSplitAcrossRunsIsJoinedWithoutExtraSpaces()
    {
        var paragraph = new Paragraph(
            new Run(new Text("Yönet")),
            new Run(new RunProperties(new Bold()), new Text("melik")),
            new Run(new Text(" metni") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }));

        var blocks = Parse(new DocxBuilder().Element(paragraph));

        Assert.Equal("Yönetmelik metni", Assert.IsType<ParagraphBlock>(Assert.Single(blocks)).Text);
    }

    // Text deleted with "track changes" on is still in the file, as w:delText.
    [Fact]
    public void TextDeletedUnderTrackChangesIsLeftOut()
    {
        var paragraph = new Paragraph(
            new Run(new Text("İzin süresi ") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }),
            new DeletedRun(new Run(new DeletedText("10"))),
            new InsertedRun(new Run(new Text("14"))),
            new Run(new Text(" gündür.") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }));

        var blocks = Parse(new DocxBuilder().Element(paragraph));

        Assert.Equal("İzin süresi 14 gündür.", Assert.IsType<ParagraphBlock>(Assert.Single(blocks)).Text);
    }

    [Fact]
    public void ParagraphsAndTablesKeepTheirOrder()
    {
        var blocks = Parse(new DocxBuilder()
            .Paragraph("Önce")
            .Table(firstRowIsHeader: false, [Cell("a"), Cell("b")])
            .Paragraph("Sonra"));

        Assert.Collection(
            blocks,
            block => Assert.Equal("Önce", Assert.IsType<ParagraphBlock>(block).Text),
            block => Assert.IsType<TableBlock>(block),
            block => Assert.Equal("Sonra", Assert.IsType<ParagraphBlock>(block).Text));
    }

    [Fact]
    public void TableWithAMarkedHeaderRowReportsIt()
    {
        var table = ParseSingleTable(new DocxBuilder().Table(
            firstRowIsHeader: true,
            [Cell("Kıdem"), Cell("Gün")],
            [Cell("1 yıl"), Cell("14")]));

        Assert.True(table.HasHeaderRow);
        Assert.Equal(["Kıdem", "Gün"], table.Rows[0]);
        Assert.Equal(["1 yıl", "14"], table.Rows[1]);
    }

    // Only Word's explicit "repeat as header row" mark counts; the parser does not guess.
    [Fact]
    public void TableWithoutAMarkedHeaderRowHasNone()
    {
        var table = ParseSingleTable(new DocxBuilder().Table(
            firstRowIsHeader: false,
            [Cell("Kıdem"), Cell("Gün")],
            [Cell("1 yıl"), Cell("14")]));

        Assert.False(table.HasHeaderRow);
        Assert.Equal(2, table.Rows.Count);
    }

    [Fact]
    public void RowsWithoutAnyTextAreDropped()
    {
        var table = ParseSingleTable(new DocxBuilder().Table(
            firstRowIsHeader: false,
            [Cell("a"), Cell("b")],
            [Cell(string.Empty), Cell(" ")],
            [Cell("c"), Cell("d")]));

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["c", "d"], table.Rows[1]);
    }

    // An empty cell keeps its place, so columns stay aligned.
    [Fact]
    public void EmptyCellInARowWithTextIsKept()
    {
        var table = ParseSingleTable(new DocxBuilder().Table(
            firstRowIsHeader: false,
            [Cell("a"), Cell(string.Empty), Cell("c")]));

        Assert.Equal(["a", string.Empty, "c"], table.Rows[0]);
    }

    [Fact]
    public void SeveralParagraphsInOneCellAreJoinedWithASpace()
    {
        var table = ParseSingleTable(new DocxBuilder().Table(
            firstRowIsHeader: false,
            [["Birinci satır", "İkinci satır"], Cell("x")]));

        Assert.Equal("Birinci satır İkinci satır", table.Rows[0][0]);
    }

    [Fact]
    public void TableWithoutAnyTextProducesNoBlock()
    {
        var blocks = Parse(new DocxBuilder()
            .Paragraph("Metin")
            .Table(firstRowIsHeader: false, [Cell(string.Empty), Cell(string.Empty)]));

        Assert.IsType<ParagraphBlock>(Assert.Single(blocks));
    }

    [Fact]
    public void EveryBlockHasNoPageNumber()
    {
        var blocks = Parse(new DocxBuilder()
            .Paragraph("Başlık", outlineLevel: 0)
            .Paragraph("Metin")
            .Table(firstRowIsHeader: false, [Cell("a")]));

        Assert.All(blocks, block => Assert.Null(block.PageNumber));
    }

    // ---- Known gaps -------------------------------------------------------------------
    // Found while writing these tests. Each test states the behaviour that seems right and
    // is skipped because the parser does not do it yet; remove Skip when it is fixed (or
    // change the expectation if another behaviour is chosen).

    private const string KnownGap = "Known gap in DocxDocumentParser; see the pull request that added this test.";

    // Shift+Enter inside a paragraph. Was a known gap: the two lines were glued ("satırikinci").
    [Fact]
    public void LineBreakInsideAParagraphSeparatesWords()
    {
        var paragraph = new Paragraph(new Run(new Text("Birinci satır"), new Break(), new Text("ikinci satır")));

        var blocks = Parse(new DocxBuilder().Element(paragraph));

        Assert.Equal("Birinci satır ikinci satır", Assert.IsType<ParagraphBlock>(Assert.Single(blocks)).Text);
    }

    // Was a known gap: the tab disappeared ("Ad:Ayşe").
    [Fact]
    public void TabInsideAParagraphSeparatesWords()
    {
        var paragraph = new Paragraph(new Run(new Text("Ad:"), new TabChar(), new Text("Ayşe")));

        var blocks = Parse(new DocxBuilder().Element(paragraph));

        Assert.Equal("Ad: Ayşe", Assert.IsType<ParagraphBlock>(Assert.Single(blocks)).Text);
    }

    // Content controls (w:sdt) wrap ordinary paragraphs in templates, forms, cover pages
    // and tables of contents. Was a known gap: everything inside one was silently dropped.
    [Fact]
    public void TextInsideAContentControlIsRead()
    {
        var contentControl = new SdtBlock(
            new SdtContentBlock(new Paragraph(new Run(new Text("Kontrol içindeki metin")))));

        var blocks = Parse(new DocxBuilder()
            .Paragraph("Önce")
            .Element(contentControl)
            .Paragraph("Sonra"));

        Assert.Equal(
            ["Önce", "Kontrol içindeki metin", "Sonra"],
            blocks.Cast<ParagraphBlock>().Select(p => p.Text));
    }

    // Was a known gap: the inner table's text was lost.
    [Fact]
    public void TextOfATableNestedInACellIsKept()
    {
        var inner = new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("iç"))))));
        var outer = new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("dış"))), inner)));

        var table = ParseSingleTable(new DocxBuilder().Element(outer));

        Assert.Contains("dış", table.Rows[0][0]);
        Assert.Contains("iç", table.Rows[0][0]);
    }

    private static IReadOnlyList<DocumentBlock> Parse(DocxBuilder builder)
    {
        using var file = builder.Build();

        return new DocxDocumentParser().Parse(file).Blocks;
    }

    private static TableBlock ParseSingleTable(DocxBuilder builder) =>
        Assert.IsType<TableBlock>(Assert.Single(Parse(builder)));
}
