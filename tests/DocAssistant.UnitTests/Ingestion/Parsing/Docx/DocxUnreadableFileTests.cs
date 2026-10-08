using System.IO.Compression;
using System.Text;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.Api.Modules.Ingestion.Parsing.Docx;
using static DocAssistant.UnitTests.Ingestion.Parsing.Docx.DocxBuilder;

namespace DocAssistant.UnitTests.Ingestion.Parsing.Docx;

// Files the parser cannot turn into text, and how it treats the caller's stream. The
// messages matter: they are stored in documents.failure_reason and shown to the user
// (docs/decisions.md #24).
public class DocxUnreadableFileTests
{
    [Fact]
    public void FileThatIsNotADocxIsReportedAsUnreadable()
    {
        using var file = new MemoryStream(Encoding.UTF8.GetBytes("bu bir Word belgesi değil"));

        AssertFailsWith(DocxDocumentParser.UnreadableFileMessage, file);
    }

    [Fact]
    public void EmptyFileIsReportedAsUnreadable()
    {
        using var file = new MemoryStream();

        AssertFailsWith(DocxDocumentParser.UnreadableFileMessage, file);
    }

    // The sample PDF: a real file of the other supported type.
    [Fact]
    public void PdfFileIsReportedAsUnreadable()
    {
        using var file = TestData.Open(TestData.SamplePdf);

        AssertFailsWith(DocxDocumentParser.UnreadableFileMessage, file);
    }

    // A spreadsheet is also a zip of XML parts, but not a Word document.
    [Fact]
    public void ZipWithoutAWordDocumentIsReportedAsUnreadable()
    {
        using var file = new MemoryStream();
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("xl/workbook.xml").Open();
            entry.Write("<workbook/>"u8);
        }

        file.Position = 0;

        AssertFailsWith(DocxDocumentParser.UnreadableFileMessage, file);
    }

    [Fact]
    public void TruncatedDocxIsReportedAsUnreadable()
    {
        using var whole = new DocxBuilder().Paragraph("Metin").Build();
        using var file = new MemoryStream(whole.ToArray()[..(int)(whole.Length / 2)]);

        AssertFailsWith(DocxDocumentParser.UnreadableFileMessage, file);
    }

    // Password-protected Word files and legacy .doc files both start with these bytes.
    [Fact]
    public void PasswordProtectedOrLegacyFileGetsItsOwnMessage()
    {
        byte[] compoundFile = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00, 0x00, 0x00];
        using var file = new MemoryStream(compoundFile);

        AssertFailsWith(DocxDocumentParser.PasswordProtectedOrLegacyMessage, file);
    }

    // decisions #23, #24: "done, 0 chunks" would hide the problem from the user.
    [Fact]
    public void DocumentWithoutAnyTextIsRejected()
    {
        using var file = new DocxBuilder()
            .Paragraph(string.Empty)
            .Table(firstRowIsHeader: false, [Cell(string.Empty)])
            .Build();

        AssertFailsWith(DocxDocumentParser.NoTextMessage, file);
    }

    // Messages go to end users: no exception type names, stack traces or paths.
    [Theory]
    [InlineData(DocxDocumentParser.UnreadableFileMessage)]
    [InlineData(DocxDocumentParser.PasswordProtectedOrLegacyMessage)]
    [InlineData(DocxDocumentParser.NoTextMessage)]
    public void MessagesAreWrittenForTheUser(string message)
    {
        Assert.DoesNotContain("Exception", message);
        Assert.DoesNotContain("\\", message);
        Assert.EndsWith(".", message);
        Assert.True(message.Length <= 500, "Longer than documents.failure_reason allows.");
    }

    // The original error is kept for the logs, even though the user never sees it.
    [Fact]
    public void UnreadableFileKeepsTheOriginalErrorAsInnerException()
    {
        using var file = new MemoryStream(Encoding.UTF8.GetBytes("bozuk"));

        var error = Assert.Throws<DocumentParseException>(() => new DocxDocumentParser().Parse(file));

        Assert.NotNull(error.InnerException);
    }

    // File storage may hand over a stream that cannot seek (IDocumentParser contract).
    [Fact]
    public void ReadsFromAStreamThatCannotSeek()
    {
        using var source = new DocxBuilder().Paragraph("Metin").Build();
        using var file = new ForwardOnlyStream(source);

        var blocks = new DocxDocumentParser().Parse(file).Blocks;

        Assert.Equal("Metin", Assert.IsType<ParagraphBlock>(Assert.Single(blocks)).Text);
    }

    // IDocumentParser: "The caller owns the stream and disposes it."
    [Fact]
    public void LeavesTheCallersStreamOpen()
    {
        using var file = new DocxBuilder().Paragraph("Metin").Build();

        new DocxDocumentParser().Parse(file);

        Assert.True(file.CanRead);
    }

    [Fact]
    public void LeavesTheCallersStreamOpenWhenParsingFails()
    {
        using var file = new MemoryStream(Encoding.UTF8.GetBytes("bozuk"));

        Assert.Throws<DocumentParseException>(() => new DocxDocumentParser().Parse(file));

        Assert.True(file.CanRead);
    }

    private static void AssertFailsWith(string expectedMessage, Stream file)
    {
        var error = Assert.Throws<DocumentParseException>(() => new DocxDocumentParser().Parse(file));

        Assert.Equal(expectedMessage, error.Message);
    }
}
