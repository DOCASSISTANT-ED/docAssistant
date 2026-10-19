using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;

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
        // answer out, whichever model or provider produces it. The provider is chosen by
        // a setting (#68), and tests replace the client with a fake. Creating either
        // client opens no connection.
        services.AddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ChatModelOptions>>().Value;

            return options.Provider switch
            {
                ChatProvider.OpenAICompatible => new OpenAIClient(
                        new ApiKeyCredential(options.ApiKey!),
                        new OpenAIClientOptions { Endpoint = new Uri(options.BaseUrl) })
                    .GetChatClient(options.Model)
                    .AsIChatClient(),

                _ => new OllamaApiClient(new Uri(options.BaseUrl), options.Model),
            };
        });

        return services;
    }
}
