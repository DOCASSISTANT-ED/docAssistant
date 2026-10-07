using System.IO.Compression;

namespace DocAssistant.Api.Modules.Documents;

// Decides what an uploaded file really is by looking at its bytes (docs/decisions.md #32).
// The file name's extension and the Content-Type header are whatever the client says
// they are, so neither is trusted.
public static class DocumentFileType
{
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    // Every zip-based format (docx, xlsx, pptx, plain zip) starts with these bytes.
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    // Returns one of DocumentContentTypes, or null for anything else. The stream must be
    // seekable and is left at its start.
    public static async Task<string?> DetectAsync(Stream content, CancellationToken cancellationToken = default)
    {
        content.Position = 0;

        var header = new byte[8];
        var read = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        var start = header.AsSpan(0, read);

        string? contentType = null;

        if (start.StartsWith(PdfSignature))
        {
            contentType = DocumentContentTypes.Pdf;
        }
        else if (start.StartsWith(ZipSignature) && IsWordDocument(content))
        {
            contentType = DocumentContentTypes.Docx;
        }

        content.Position = 0;

        return contentType;
    }

    // A .docx is a zip whose main part is word/document.xml; a spreadsheet or a plain
    // zip is also a zip but has no such entry.
    private static bool IsWordDocument(Stream content)
    {
        content.Position = 0;

        try
        {
            using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);

            return zip.GetEntry("word/document.xml") is not null;
        }
        catch (InvalidDataException)
        {
            // Starts like a zip but is not a readable one.
            return false;
        }
    }
}
