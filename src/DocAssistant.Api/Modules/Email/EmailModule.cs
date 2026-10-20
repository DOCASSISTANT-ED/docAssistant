namespace DocAssistant.Api.Modules.Email;

public static class EmailModule
{
    public static IServiceCollection AddEmailModule(this IServiceCollection services)
    {
        // Checked at startup: a missing SMTP setting should stop the app from starting, not
        // surface as a verification code that never arrives.
        services.AddOptions<EmailOptions>()
            .BindConfiguration(EmailOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Creating the sender opens no connection; each e-mail opens its own.
        services.AddSingleton<IEmailSender, SmtpEmailSender>();

        return services;
    }
}
