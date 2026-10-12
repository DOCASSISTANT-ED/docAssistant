namespace DocAssistant.Eval;

// Runs the evaluation set against the real search pipeline (docs/decisions.md #27, #40,
// #47, #48). Needs Docker (a throwaway PostgreSQL) and Ollama with the embedding model.
//
//   dotnet run --project eval/DocAssistant.Eval
//   dotnet run --project eval/DocAssistant.Eval -- --embedding-url http://localhost:11434 --embedding-model bge-m3
//
// A class with Main instead of top-level statements: those would create a type named
// Program, which is also the name of the API's entry point that EvalApi hosts.
internal static class EvalRunner
{
    private static readonly string[] Formats = ["docx", "pdf"];

    private static async Task Main(string[] args)
    {
        var embeddingUrl = Option(args, "--embedding-url") ?? "http://localhost:11434";
        var embeddingModel = Option(args, "--embedding-model") ?? "bge-m3";

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var paths = RepoPaths.Find();
        var questions = QuestionSet.Load(paths.Questions);
        var ranAt = DateTimeOffset.Now;

        Console.WriteLine("Starting a throwaway database...");
        await using var database = await EvalDatabase.StartAsync(paths);
        await using var api = new EvalApi(paths, database.AppConnectionString, embeddingUrl, embeddingModel);

        var results = new List<FormatResult>();

        foreach (var format in Formats)
        {
            Console.WriteLine($"Uploading and processing the {format.ToUpperInvariant()} documents...");
            var corpus = await Corpus.UploadAsync(api, paths, format, stop.Token);

            Console.WriteLine($"Asking the questions ({format.ToUpperInvariant()})...");
            results.Add(await Measurement.RunAsync(api, corpus, questions, stop.Token));
        }

        Report.Print(Console.Out, questions, embeddingModel, results);

        var saved = Report.Save(paths, questions, embeddingModel, ranAt, results);
        Console.WriteLine();
        Console.WriteLine($"Details: {Path.GetRelativePath(paths.Root, saved)}");
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
