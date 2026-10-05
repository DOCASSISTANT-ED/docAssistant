namespace DocAssistant.Api.Modules.Documents;

// Stored as text in the database, so renaming a value needs a data migration.
public enum DocumentStatus
{
    Pending,
    Processing,
    Done,
    Failed,
}
