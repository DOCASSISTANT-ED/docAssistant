namespace DocAssistant.Api.Modules.Ingestion.Parsing;

// The file format a document was parsed from. Written to every chunk so results can be
// compared per format (docs/decisions.md #27).
public enum DocumentSourceType
{
    Pdf,
    Docx,
}
