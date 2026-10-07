namespace DocAssistant.Api.Modules.Documents;

// The file types that can be uploaded (docs/decisions.md #32), as stored in
// documents.content_type.
public static class DocumentContentTypes
{
    public const string Pdf = "application/pdf";

    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
}
