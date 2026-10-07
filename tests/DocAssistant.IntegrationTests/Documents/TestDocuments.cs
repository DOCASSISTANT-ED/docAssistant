using DocAssistant.Api.Modules.Documents;

namespace DocAssistant.IntegrationTests.Documents;

// Builds valid document rows for tests that are not about uploading. A document needs a
// real uploader (foreign key to users) and a unique storage key.
internal static class TestDocuments
{
    public const long SizeBytes = 1024;

    public static string NewStorageKey() => $"tests/{Guid.NewGuid():N}/original.pdf";

    public static Document New(string title, Guid uploadedByUserId, Guid? tenantId = null)
    {
        var document = new Document
        {
            Title = title,
            FileName = $"{title}.pdf",
            ContentType = DocumentContentTypes.Pdf,
            SizeBytes = SizeBytes,
            StorageKey = NewStorageKey(),
            UploadedByUserId = uploadedByUserId,
        };

        if (tenantId is not null)
        {
            document.TenantId = tenantId.Value;
        }

        return document;
    }
}
