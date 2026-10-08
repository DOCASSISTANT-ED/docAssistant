using System.Text;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.Api.Modules.Ingestion.Parsing.Pdf;

namespace DocAssistant.UnitTests.Ingestion.Parsing.Pdf;

// Files the parser cannot turn into text, and how it treats the caller's stream. The
// messages matter: they are stored in documents.failure_reason and shown to the user
// (docs/decisions.md #24).
//
// Not covered: a password-protected PDF (PasswordProtectedMessage). PdfPig cannot write
// encrypted files, so this needs a prepared file in tests/TestData.
public class PdfUnreadableFileTests
{
    [Fact]
    public void FileThatIsNotAPdfIsReportedAsUnreadable()
    {
        using var file = new MemoryStream(Encoding.UTF8.GetBytes("bu bir PDF belgesi değil"));

        AssertFailsWith(PdfDocumentParser.UnreadableFileMessage, file);
    }

    [Fact]
    public void EmptyFileIsReportedAsUnreadable()
    {
        using var file = new MemoryStream();

        AssertFailsWith(PdfDocumentParser.UnreadableFileMessage, file);
    }

    // The sample DOCX: a real file of the other supported type.
    [Fact]
    public void DocxFileIsReportedAsUnreadable()
    {
        using var file = TestData.Open(TestData.SampleDocx);

        AssertFailsWith(PdfDocumentParser.UnreadableFileMessage, file);
    }

    // decisions #23: a scan has pages but no text; "done, 0 chunks" would hide that.
    [Fact]
    public void PdfWithoutAnyTextIsRejectedAsAScan()
    {
        using var file = new PdfBuilder().Page().Build();

        AssertFailsWith(PdfDocumentParser.NoTextMessage, file);
    }

    // Messages go to end users: no exception type names, stack traces or paths.
    [Theory]
    [InlineData(PdfDocumentParser.UnreadableFileMessage)]
    [InlineData(PdfDocumentParser.PasswordProtectedMessage)]
    [InlineData(PdfDocumentParser.NoPagesMessage)]
    [InlineData(PdfDocumentParser.NoTextMessage)]
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

        var error = Assert.Throws<DocumentParseException>(() => new PdfDocumentParser().Parse(file));

        Assert.NotNull(error.InnerException);
    }

    // IDocumentParser: "The caller owns the stream and disposes it."
    [Fact]
    public void LeavesTheCallersStreamOpen()
    {
        using var file = new PdfBuilder().Paragraph("Text").Build();

        new PdfDocumentParser().Parse(file);

        Assert.True(file.CanRead);
    }

    [Fact]
    public void LeavesTheCallersStreamOpenWhenParsingFails()
    {
        using var file = new MemoryStream(Encoding.UTF8.GetBytes("bozuk"));

        Assert.Throws<DocumentParseException>(() => new PdfDocumentParser().Parse(file));

        Assert.True(file.CanRead);
    }

    // File storage may hand over a stream that cannot seek (IDocumentParser contract).
    [Fact]
    public void ReadsFromAStreamThatCannotSeek()
    {
        using var source = new PdfBuilder().Paragraph("Text").Build();
        using var file = new ForwardOnlyStream(source);

        var blocks = new PdfDocumentParser().Parse(file).Blocks;

        Assert.Equal("Text", Assert.IsType<ParagraphBlock>(Assert.Single(blocks)).Text);
    }

    // Half a PDF: the end of the file, where PDF keeps its table of contents, is missing.
    [Fact]
    public void TruncatedPdfIsReportedAsUnreadable()
    {
        using var whole = TestData.Open(TestData.SamplePdf);
        using var copy = new MemoryStream();
        whole.CopyTo(copy);
        using var file = new MemoryStream(copy.ToArray()[..(int)(copy.Length / 2)]);

        AssertFailsWith(PdfDocumentParser.UnreadableFileMessage, file);
    }

    private static void AssertFailsWith(string expectedMessage, Stream file)
    {
        var error = Assert.Throws<DocumentParseException>(() => new PdfDocumentParser().Parse(file));

        Assert.Equal(expectedMessage, error.Message);
    }
}
