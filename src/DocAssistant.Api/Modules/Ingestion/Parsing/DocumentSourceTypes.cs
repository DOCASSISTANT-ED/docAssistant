using DocAssistant.Api.Modules.Documents;

namespace DocAssistant.Api.Modules.Ingestion.Parsing;

public static class DocumentSourceTypes
{
    // Maps documents.content_type to the parser that reads it. False for any other value.
    public static bool TryFromContentType(string contentType, out DocumentSourceType sourceType)
    {
        switch (contentType)
        {
            case DocumentContentTypes.Pdf:
                sourceType = DocumentSourceType.Pdf;
                return true;

            case DocumentContentTypes.Docx:
                sourceType = DocumentSourceType.Docx;
                return true;

            default:
                sourceType = default;
                return false;
        }
    }
}
