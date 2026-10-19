using System.Text.RegularExpressions;

namespace DocAssistant.Api.Modules.Tenants;

public partial class Tenant
{
    private string _name = string.Empty;

    public Guid Id { get; set; }

    // Setting the name also sets NormalizedName, so the two can never disagree.
    public required string Name
    {
        get => _name;
        set
        {
            _name = value;
            NormalizedName = NormalizeName(value);
        }
    }

    // The name with spaces, case and the Turkish dotted and dotless I left out of account;
    // two companies may not share it (docs/decisions.md #70).
    public string NormalizedName { get; private set; } = string.Empty;

    // The company's email rule (decisions #70): the domains its people's addresses must end
    // in, lower case and without "@"; empty when the company has no email of its own.
    public List<string> EmailDomains { get; set; } = [];

    // SHA-256 of the registration code (RegistrationCode); null until one is made.
    public string? RegistrationCodeHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // The same steps as the migration that filled the column for existing companies
    // (AddCompanyRegistration): trim, one space between words, i ı İ I all as I, upper case.
    public static string NormalizeName(string name) =>
        WhiteSpace().Replace(name.Trim(), " ")
            .Replace('i', 'I').Replace('ı', 'I').Replace('İ', 'I')
            .ToUpperInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
