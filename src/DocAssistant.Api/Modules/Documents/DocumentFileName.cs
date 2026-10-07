namespace DocAssistant.Api.Modules.Documents;

// Cleans up the file name a client sent. The result is only stored and shown; it is never
// used to build a path (docs/decisions.md #33).
public static class DocumentFileName
{
    private const string Fallback = "document";

    // Drops any directory part (some clients send a full path), control characters and
    // surrounding spaces, and cuts the name to the column length.
    public static string Clean(string? fileName)
    {
        var name = fileName ?? string.Empty;

        // Path.GetFileName only knows the separators of the OS the server runs on; the
        // client may use either.
        var lastSeparator = name.LastIndexOfAny(['/', '\\']);
        if (lastSeparator >= 0)
        {
            name = name[(lastSeparator + 1)..];
        }

        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();

        if (name.Length == 0)
        {
            return Fallback;
        }

        return name.Length <= DocumentConfiguration.FileNameMaxLength
            ? name
            : name[..DocumentConfiguration.FileNameMaxLength];
    }

    // The display name: the cleaned file name without its extension.
    public static string ToTitle(string cleanFileName)
    {
        var title = Path.GetFileNameWithoutExtension(cleanFileName).Trim();

        if (title.Length == 0)
        {
            title = cleanFileName;
        }

        return title.Length <= DocumentConfiguration.TitleMaxLength
            ? title
            : title[..DocumentConfiguration.TitleMaxLength];
    }
}
