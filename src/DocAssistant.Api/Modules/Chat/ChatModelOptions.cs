using System.ComponentModel.DataAnnotations;

namespace DocAssistant.Api.Modules.Chat;

// Where the chat model runs and which model it is (docs/decisions.md #10, #55, #61, #68).
// Not named ChatOptions: Microsoft.Extensions.AI has a type of that name.
public sealed class ChatModelOptions : IValidatableObject
{
    public const string SectionName = "Chat";

    // Who runs the model (decisions #68). Ollama is the default and what development uses.
    public ChatProvider Provider { get; init; } = ChatProvider.Ollama;

    // The Ollama server in development, e.g. http://localhost:11434; for an OpenAI
    // compatible API its base address, e.g. https://api.groq.com/openai/v1.
    [Required]
    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    // The model's name at the provider, e.g. llama3.1:8b in Ollama.
    [Required]
    public string Model { get; init; } = string.Empty;

    // The key of an OpenAI compatible API. Kept in user-secrets or an environment
    // variable (Chat__ApiKey), never in a file in the repository; Ollama needs none.
    public string? ApiKey { get; init; }

    // How much text the model reads at once: instructions, sources, history and question
    // together. Set explicitly (decisions #61): Ollama's own default is smaller than a
    // question with eight sources, and what does not fit is cut off without an error.
    [Range(2048, 131072)]
    public int ContextTokens { get; init; } = 8192;

    // The longest answer the model may write (decisions #64). Answers from the documents
    // are far shorter; the limit is there to stop a model that has started to repeat
    // itself, which would otherwise write until the user stops it.
    [Range(64, 8192)]
    public int MaxAnswerTokens { get; init; } = 1024;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Provider == ChatProvider.OpenAICompatible && string.IsNullOrWhiteSpace(ApiKey))
        {
            yield return new ValidationResult(
                "An OpenAI compatible provider needs Chat:ApiKey.",
                [nameof(ApiKey)]);
        }
    }
}

public enum ChatProvider
{
    Ollama,

    // An API that speaks OpenAI's chat completions protocol, such as Groq.
    OpenAICompatible,
}
