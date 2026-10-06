using System.ComponentModel.DataAnnotations;
using DocAssistant.Api.Modules.Tenants;

namespace DocAssistant.Api.Modules.Identity;

public static class PasswordRules
{
    public const int MinLength = 8;

    // Hashing is deliberately slow; an upper bound stops huge inputs from being used to
    // tie up the server.
    public const int MaxLength = 128;
}

public sealed record RegisterRequest
{
    [Required]
    [MaxLength(TenantConfiguration.NameMaxLength)]
    public string CompanyName { get; init; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(UserConfiguration.EmailMaxLength)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [MinLength(PasswordRules.MinLength)]
    [MaxLength(PasswordRules.MaxLength)]
    public string Password { get; init; } = string.Empty;
}

public sealed record LoginRequest
{
    [Required]
    [MaxLength(UserConfiguration.EmailMaxLength)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [MaxLength(PasswordRules.MaxLength)]
    public string Password { get; init; } = string.Empty;
}
