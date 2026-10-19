namespace DocAssistant.Eval;

// The files the run reads live in the repository, not next to the built program.
internal sealed class RepoPaths
{
    private RepoPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string Questions => Path.Combine(Root, "eval", "questions.json");

    public string Conversations => Path.Combine(Root, "eval", "conversations.json");

    public string Results => Path.Combine(Root, "eval", "results");

    public string SampleDocuments => Path.Combine(Root, "tests", "TestData");

    public string ApiProject => Path.Combine(Root, "src", "DocAssistant.Api");

    public string DatabaseInitScripts => Path.Combine(Root, "docker", "postgres", "init");

    // Walks up from the build output folder (eval/DocAssistant.Eval/bin/...) to the repo root.
    public static RepoPaths Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "eval", "questions.json")))
            {
                return new RepoPaths(dir.FullName);
            }
        }

        throw new DirectoryNotFoundException("eval/questions.json was not found above the program's folder.");
    }
}
