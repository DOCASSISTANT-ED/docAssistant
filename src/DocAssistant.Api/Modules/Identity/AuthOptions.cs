namespace DocAssistant.Api.Modules.Identity;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    // Whether anyone may open a company with POST /auth/register (docs/decisions.md #70).
    // On in development, tests and the evaluation run, where it is the only quick way to
    // make a company; off in production, where only the platform admin opens companies.
    // Off when not set, so a deployment that forgets the setting stays closed.
    public bool OpenRegistration { get; init; }
}
