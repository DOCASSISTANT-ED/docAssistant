namespace DocAssistant.Eval;

// Runs the evaluation set against the real search pipeline (docs/decisions.md #27, #40,
// #47, #48) and, when asked, against the chat endpoint (#62). Needs Docker (a throwaway
// PostgreSQL) and Ollama with the embedding model; --answers also needs the chat model.
//
//   dotnet run --project eval/DocAssistant.Eval
//   dotnet run --project eval/DocAssistant.Eval -- --embedding-url http://localhost:11434 --embedding-model bge-m3
//   dotnet run --project eval/DocAssistant.Eval -- --answers                 every question, about 11 minutes
//   dotnet run --project eval/DocAssistant.Eval -- --answers --sample 5      5 per question type, about 3 minutes
//   dotnet run --project eval/DocAssistant.Eval -- --answers --chat-model llama3.1:8b --chat-url http://localhost:11434
//   dotnet run --project eval/DocAssistant.Eval -- --typos                   one spelling mistake per question (Typos)
//
// A class with Main instead of top-level statements: those would create a type named
// Program, which is also the name of the API's entry point that EvalApi hosts.
internal static class EvalRunner
{
    private static readonly string[] Formats = ["docx", "pdf"];

    // Answers are measured on one format: what the model does depends on the chunks'
    // text, which is nearly the same in both, and asking twice would double a long run.
    private const string AnswerFormat = "docx";

    private static async Task Main(string[] args)
    {
        var embeddingUrl = Option(args, "--embedding-url") ?? "http://localhost:11434";
        var embeddingModel = Option(args, "--embedding-model") ?? "bge-m3";

        // decisions #61: the development chat model.
        var chatUrl = Option(args, "--chat-url") ?? "http://localhost:11434";
        var chatModel = Option(args, "--chat-model") ?? "llama3.1:8b";
        var measureAnswers = args.Contains("--answers");
        var sample = Option(args, "--sample") is { } perType ? int.Parse(perType) : (int?)null;

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var paths = RepoPaths.Find();
        var questions = QuestionSet.Load(paths.Questions);
        var typos = args.Contains("--typos") ? Typos.Make(questions) : null;

        if (typos is not null)
        {
            questions = Typos.Apply(questions, typos);
        }

        var ranAt = DateTimeOffset.Now;

        Console.WriteLine("Starting a throwaway database...");
        await using var database = await EvalDatabase.StartAsync(paths);
        await using var api = new EvalApi(paths, database.AppConnectionString, embeddingUrl, embeddingModel, chatUrl, chatModel);

        var results = new List<FormatResult>();
        AnswerRun? answers = null;

        foreach (var format in Formats)
        {
            Console.WriteLine($"Uploading and processing the {format.ToUpperInvariant()} documents...");
            var corpus = await Corpus.UploadAsync(api, paths, format, stop.Token);

            Console.WriteLine($"Asking the questions ({format.ToUpperInvariant()})...");
            results.Add(await Measurement.RunAsync(api, corpus, questions, stop.Token));

            if (measureAnswers && format == AnswerFormat)
            {
                var asked = sample is { } count ? AnswerMeasurement.Sample(questions.Questions, count) : questions.Questions;

                Console.WriteLine($"Asking {asked.Count} questions through the chat endpoint ({chatModel})...");
                answers = await AnswerMeasurement.RunAsync(api, corpus, asked, chatModel, Console.Out, stop.Token);
            }
        }

        Report.Print(Console.Out, questions, embeddingModel, typos, results);

        if (answers is not null)
        {
            Report.PrintAnswers(Console.Out, answers);
        }

        var saved = Report.Save(paths, questions, embeddingModel, typos, ranAt, results, answers);
        Console.WriteLine();
        Console.WriteLine($"Details: {Path.GetRelativePath(paths.Root, saved)}");
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
