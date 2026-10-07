using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocAssistant.Api.Modules.Ingestion.Parsing.Docx;

// A DOCX file is a zip of XML parts. Unlike a PDF it stores structure, not just looks:
// paragraphs, their styles and tables are marked explicitly (docs/decisions.md #21).
// It stores no page numbers, so every block's PageNumber is null (decisions #22).
public sealed class DocxDocumentParser : IDocumentParser
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

        var blocks = ReadBlocks(body);

        // Failing loudly beats reporting "done, 0 chunks" (decisions #23, #24).
        if (blocks.Count == 0)
        {
            throw new DocumentParseException(NoTextMessage);
        }

        return new ParsedDocument(blocks);
    }

    private static List<DocumentBlock> ReadBlocks(Body body)
    {
        var blocks = new List<DocumentBlock>();

        foreach (var paragraph in body.Elements<Paragraph>())
        {
            var text = TextOf(paragraph);
            if (text.Length > 0)
            {
                blocks.Add(new ParagraphBlock(text, PageNumber: null));
            }
        }

        return blocks;
    }

    // Only the visible text runs (w:t): field codes such as PAGE and text deleted under
    // track changes are separate elements and are left out.
    private static string TextOf(Paragraph paragraph) =>
        string.Concat(paragraph.Descendants<Text>().Select(text => text.Text)).Trim();

    private static bool StartsWith(Stream stream, byte[] signature)
    {
        Span<byte> start = stackalloc byte[signature.Length];
        var read = stream.Read(start);
        stream.Position = 0;

        return read == signature.Length && start.SequenceEqual(signature);
    }
}
