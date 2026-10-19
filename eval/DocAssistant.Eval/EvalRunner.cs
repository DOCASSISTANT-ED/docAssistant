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
//   dotnet run --project eval/DocAssistant.Eval -- --answers --ids ik-023,uc-015   only these questions are asked
//   dotnet run --project eval/DocAssistant.Eval -- --conversations         eval/conversations.json, turn by turn
//
// An OpenAI compatible API instead of Ollama (decisions #68); the key is read from the
// CHAT_API_KEY environment variable, and --chat-delay pauses between questions to stay
// under a per-minute token limit:
//
//   dotnet run --project eval/DocAssistant.Eval -- --answers --chat-provider openai
//       --chat-url https://api.groq.com/openai/v1 --chat-model openai/gpt-oss-120b --chat-delay 25
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
        var measureConversations = args.Contains("--conversations");
        var sample = Option(args, "--sample") is { } perType ? int.Parse(perType) : (int?)null;
        var ids = Option(args, "--ids")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var delay = TimeSpan.FromSeconds(Option(args, "--chat-delay") is { } seconds ? int.Parse(seconds) : 0);

        // decisions #68. The key is not taken from the command line, where it would stay
        // in the shell's history.
        var openAICompatible = Option(args, "--chat-provider") is "openai";
        var chatProvider = openAICompatible ? "OpenAICompatible" : "Ollama";
        var chatApiKey = openAICompatible ? Environment.GetEnvironmentVariable("CHAT_API_KEY") : null;

        if (openAICompatible && (string.IsNullOrWhiteSpace(chatApiKey) || Option(args, "--chat-url") is null))
        {
            Console.Error.WriteLine("--chat-provider openai needs --chat-url and the CHAT_API_KEY environment variable.");
            Environment.Exit(1);
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var paths = RepoPaths.Find();
        var questions = QuestionSet.Load(paths.Questions);
        var ranAt = DateTimeOffset.Now;

        // --ids: only these questions are asked through the chat; the search measurement
        // still covers the whole set, so its numbers stay comparable with other runs.
        var unknown = ids?.Except(questions.Questions.Select(question => question.Id)).ToList() ?? [];
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"Not in the question set: {string.Join(", ", unknown)}");
            Environment.Exit(1);
        }

        Console.WriteLine("Starting a throwaway database...");
        await using var database = await EvalDatabase.StartAsync(paths);
        await using var api = new EvalApi(
            paths, database.AppConnectionString, embeddingUrl, embeddingModel, chatUrl, chatModel, chatProvider, chatApiKey);

        var results = new List<FormatResult>();
        AnswerRun? answers = null;
        IReadOnlyList<ConversationResult>? conversations = null;

        foreach (var format in Formats)
        {
            Console.WriteLine($"Uploading and processing the {format.ToUpperInvariant()} documents...");
            var corpus = await Corpus.UploadAsync(api, paths, format, stop.Token);

            Console.WriteLine($"Asking the questions ({format.ToUpperInvariant()})...");
            results.Add(await Measurement.RunAsync(api, corpus, questions, stop.Token));

            if (measureAnswers && format == AnswerFormat)
            {
                var asked = ids is not null
                    ? questions.Questions.Where(question => ids.Contains(question.Id)).ToList()
                    : sample is { } count
                        ? AnswerMeasurement.Sample(questions.Questions, count)
                        : questions.Questions;

                Console.WriteLine($"Asking {asked.Count} questions through the chat endpoint ({chatModel})...");
                answers = await AnswerMeasurement.RunAsync(api, corpus, asked, chatModel, delay, Console.Out, stop.Token);
            }

            if (measureConversations && format == AnswerFormat)
            {
                var set = ConversationSet.Load(paths.Conversations);

                Console.WriteLine($"Asking {set.Conversations.Count} conversations through the chat endpoint ({chatModel})...");
                conversations = await ConversationMeasurement.RunAsync(api, corpus, set, delay, Console.Out, stop.Token);
            }
        }

        Report.Print(Console.Out, questions, embeddingModel, results);

        if (answers is not null)
        {
            Report.PrintAnswers(Console.Out, answers);
        }

        if (conversations is not null)
        {
            Report.PrintConversations(Console.Out, chatModel, conversations);
        }

        var saved = Report.Save(paths, questions, embeddingModel, ranAt, results, answers, conversations);
        Console.WriteLine();
        Console.WriteLine($"Details: {Path.GetRelativePath(paths.Root, saved)}");
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
