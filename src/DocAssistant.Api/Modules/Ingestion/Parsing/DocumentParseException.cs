namespace DocAssistant.Api.Modules.Ingestion.Parsing;

// Thrown by parsers for files that cannot be processed. Message is shown to the user and
// stored in documents.failure_reason (docs/decisions.md #24), so it must be written for
// them: say what is wrong with the file, never include stack traces or file paths.
public sealed class DocumentParseException : Exception
{
    public DocumentParseException(string message)
        : base(message)
    {
    }

    public DocumentParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
