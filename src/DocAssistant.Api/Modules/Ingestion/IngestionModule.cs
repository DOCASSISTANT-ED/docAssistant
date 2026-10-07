namespace DocAssistant.Api.Modules.Ingestion;

public static class IngestionModule
{
    public static IServiceCollection AddIngestionModule(this IServiceCollection services)
    {
        // One queue for the whole app: the upload endpoint (through IIngestionQueue) and the
        // background worker (through ChannelIngestionQueue) must share the same instance.
        services.AddSingleton<ChannelIngestionQueue>();
        services.AddSingleton<IIngestionQueue>(sp => sp.GetRequiredService<ChannelIngestionQueue>());

        return services;
    }
}
