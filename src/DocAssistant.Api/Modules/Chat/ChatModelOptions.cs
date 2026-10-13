using System.ComponentModel.DataAnnotations;

namespace DocAssistant.Api.Modules.Chat;

// Where the chat model runs and which model it is (docs/decisions.md #10, #55, #61).
// Not named ChatOptions: Microsoft.Extensions.AI has a type of that name.
public sealed class ChatModelOptions
{
    public const string SectionName = "Chat";

    // The Ollama server in development, e.g. http://localhost:11434.
    [Required]
    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    // The model's name in Ollama, e.g. llama3.1:8b.
    [Required]
    public string Model { get; init; } = string.Empty;

    // How much text the model reads at once: instructions, sources, history and question
    // together. Set explicitly (decisions #61): Ollama's own default is smaller than a
    // question with eight sources, and what does not fit is cut off without an error.
    [Range(2048, 131072)]
    public int ContextTokens { get; init; } = 8192;
}
