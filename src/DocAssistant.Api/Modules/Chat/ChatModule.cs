using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace DocAssistant.Api.Modules.Chat;

public static class ChatModule
{
    public static IServiceCollection AddChatModule(this IServiceCollection services)
    {
        // Checked at startup, like the embedding settings: a missing model name should
        // stop the app from starting, not fail the first question.
        services.AddOptions<ChatModelOptions>()
            .BindConfiguration(ChatModelOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The rest of the app sees only IChatClient (docs/decisions.md #10): text in,
        // answer out, whichever model or provider produces it. Moving to an API model in
        // production changes this registration only, and tests replace it with a fake.
        // Creating the client opens no connection.
        services.AddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ChatModelOptions>>().Value;

            return new OllamaApiClient(new Uri(options.BaseUrl), options.Model);
        });

        return services;
    }
}
