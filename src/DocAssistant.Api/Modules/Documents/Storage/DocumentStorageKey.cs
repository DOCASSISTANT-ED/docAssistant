namespace DocAssistant.Api.Modules.Documents.Storage;

public static class DocumentStorageKey
{
    // tenants/{tenantId}/documents/{documentId}/original.{pdf|docx}  (docs/decisions.md #33)
    //
    // Built only from ids we generated and the validated content type. The user's file
    // name is never part of it, so odd or hostile names cannot affect where a file lands.
    // The tenant comes first: all of a tenant's files share one prefix.
    public static string ForOriginal(Guid tenantId, Guid documentId, string contentType)
    {
        var extension = contentType switch
        {
            DocumentContentTypes.Pdf => "pdf",
            DocumentContentTypes.Docx => "docx",
            _ => throw new ArgumentException($"Unsupported content type '{contentType}'.", nameof(contentType)),
        };

        return $"tenants/{tenantId:D}/documents/{documentId:D}/original.{extension}";
    }
}
