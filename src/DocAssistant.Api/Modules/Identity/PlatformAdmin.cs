using DocAssistant.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocAssistant.Api.Modules.Identity;

// The platform admin (docs/decisions.md #70): runs the platform, not a company. Signs in
// without an organization, so no company's data is visible (query filters and RLS match
// nothing without a tenant), and may only register companies.
public static class PlatformAdmin
{
    // The role claim of a platform admin's token. Not a MembershipRole: it belongs to no
    // membership.
    public const string Role = "PlatformAdmin";

    public const string CommandName = "create-platform-admin";

    // dotnet run --project src/DocAssistant.Api -- create-platform-admin <email>
    //
    // From the command line only, so there is no way over the internet to become one. An
    // existing account is made a platform admin; otherwise a new account is created with
    // the password typed at the prompt. Returns the process exit code.
    public static async Task<int> RunCommandAsync(IServiceProvider services, string[] args)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]) || !args[1].Contains('@'))
        {
            Console.Error.WriteLine($"Usage: dotnet run --project src/DocAssistant.Api -- {CommandName} <email>");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        var email = args[1].Trim();
        var normalizedEmail = User.NormalizeEmail(email);

        // No tenant outside a request (decisions #15).
        var user = await db.Users
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail);

        if (user is not null)
        {
            user.IsPlatformAdmin = true;
            await db.SaveChangesAsync();
            Console.WriteLine($"{user.Email} is now a platform admin; the password is unchanged.");
            return 0;
        }

        var password = ReadPassword("Password: ");
        if (password.Length is < PasswordRules.MinLength or > PasswordRules.MaxLength)
        {
            Console.Error.WriteLine($"The password must be {PasswordRules.MinLength} to {PasswordRules.MaxLength} characters.");
            return 1;
        }

        if (ReadPassword("Password again: ") != password)
        {
            Console.Error.WriteLine("The passwords do not match.");
            return 1;
        }

        user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = string.Empty,
            IsPlatformAdmin = true,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, password);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        Console.WriteLine($"Created platform admin {user.Email}.");
        return 0;
    }

    // Typed characters are not echoed, so the password does not stay on the screen.
    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);

        // Piped input (no console to hide characters on): read the line as it is.
        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine() ?? string.Empty;
            Console.WriteLine();
            return line;
        }

        var password = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return password.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }
    }
}
