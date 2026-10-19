using System.Security.Cryptography;
using System.Text;

namespace DocAssistant.Api.Modules.Tenants;

// A company's registration code (docs/decisions.md #70): without it nobody can register
// as one of the company's admins. Shown once when made; only its SHA-256 is stored, as
// with invitation keys (#67), so a leaked database does not give the codes away.
public static class RegistrationCode
{
    // No 0/O, 1/I/L or 5/S: the code is read aloud and typed by hand.
    private const string Alphabet = "ABCDEFGHJKMNPQRTUVWXYZ2346789";

    private const int Length = 12;

    private const int GroupLength = 4;

    // 29^12 ≈ 3.5 × 10^17 codes: guessing one is out of reach.
    public static string New()
    {
        var code = new StringBuilder(Length + (Length / GroupLength) - 1);

        for (var i = 0; i < Length; i++)
        {
            if (i > 0 && i % GroupLength == 0)
            {
                code.Append('-');
            }

            code.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }

        return code.ToString();
    }

    // "abcd efgh-jkmn" and "ABCD-EFGH-JKMN" are the same code: dashes, spaces and case
    // do not count.
    public static string Hash(string code)
    {
        var normalized = new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
