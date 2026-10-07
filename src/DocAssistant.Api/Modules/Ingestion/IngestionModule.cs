using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.Api.Modules.Ingestion.Parsing.Docx;
using DocAssistant.Api.Modules.Ingestion.Parsing.Pdf;

namespace DocAssistant.Api.Modules.Ingestion;

public static class IngestionModule
{
    public static IServiceCollection AddIngestionModule(this IServiceCollection services)
    {
        // One queue for the whole app: the upload endpoint (through IIngestionQueue) and the
        // background worker (through ChannelIngestionQueue) must share the same instance.
        services.AddSingleton<ChannelIngestionQueue>();
        services.AddSingleton<IIngestionQueue>(sp => sp.GetRequiredService<ChannelIngestionQueue>());

        services.AddHostedService<IngestionRecoveryService>();
        services.AddHostedService<IngestionWorker>();

        // Scoped: the worker resolves these from a new scope per document, together with
        // that document's AppDbContext and tenant (decisions #31).
        services.AddScoped<DocumentIngestionHandler>();
        services.AddScoped<IDocumentProcessor, PendingDocumentProcessor>(); // replaced in K4

        // One parser per file format; the worker picks the one whose SourceType matches
        // the document (DocumentSourceTypes.TryFromContentType). Parsers keep no state.
        services.AddSingleton<IDocumentParser, PdfDocumentParser>();
        services.AddSingleton<IDocumentParser, DocxDocumentParser>();

        return services;
    }
}
