namespace DocAssistant.Api.Modules.Ingestion.Parsing;

// One implementation per file format (docs/decisions.md #21).
public interface IDocumentParser
{
    DocumentSourceType SourceType { get; }

    // Reads the whole file and returns its blocks in reading order. The caller owns the
    // stream and disposes it.
    //
    // Throws DocumentParseException when the file cannot be turned into text: corrupted,
    // password-protected, empty, or a scan without extractable text (decisions #23, #24).
    ParsedDocument Parse(Stream content);
}
