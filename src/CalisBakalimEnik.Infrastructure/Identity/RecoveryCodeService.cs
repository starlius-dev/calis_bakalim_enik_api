using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace CalisBakalimEnik.Infrastructure.Identity;

/// <summary>
/// Single-use backup codes, Argon2id-hashed. Displayed exactly once at
/// generation; generating a new batch invalidates the previous one.
/// </summary>
public sealed class RecoveryCodeService
{
    public const int BatchSize = 10;

    /// <summary>Warn the user below this many remaining.</summary>
    public const int LowWaterMark = 3;

    // Crockford-ish base32 without I, L, O, U: a code is read off a screen and
    // typed by hand, and those four are the ones people get wrong.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ0123456789";

    public static string Generate()
    {
        var chars = new char[11];
        for (var i = 0; i < 11; i++)
        {
            chars[i] = i == 5 ? '-' : Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return new string(chars);
    }

    public static string Hash(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(Normalise(code), salt);

        return $"$argon2id$v=19$m=19456,t=2,p=1${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string code, string stored)
    {
        var parts = stored.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return false;

        try
        {
            var salt = Convert.FromBase64String(parts[^2]);
            var expected = Convert.FromBase64String(parts[^1]);
            var actual = Derive(Normalise(code), salt);

            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Codes are shown uppercase with a dash; accept any casing or spacing.</summary>
    private static string Normalise(string code)
        => code.Trim().ToUpperInvariant().Replace(" ", string.Empty).Replace("-", string.Empty);

    private static byte[] Derive(string code, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(code))
        {
            Salt = salt,
            DegreeOfParallelism = 1,
            MemorySize = 19456,   // 19 MiB — OWASP's minimum for Argon2id
            Iterations = 2,
        };

        return argon.GetBytes(32);
    }
}
