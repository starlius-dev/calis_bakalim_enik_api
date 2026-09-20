using System.Security.Cryptography;
using System.Text;
using CalisBakalimEnik.Application.Common.Interfaces;
using Microsoft.AspNetCore.DataProtection;

namespace CalisBakalimEnik.Infrastructure.Identity;

/// <summary>
/// RFC 6238 TOTP: SHA-1, 6 digits, 30-second step, ±1 step drift.
///
/// SHA-1 is not a weakness here — HMAC-SHA1 is unbroken, and every authenticator
/// app implements it. Choosing SHA-256 would produce codes most apps cannot
/// generate from a scanned QR.
/// </summary>
public sealed class TotpService(IDataProtectionProvider protectionProvider, IClock clock)
{
    private const int Digits = 6;
    private const int StepSeconds = 30;

    /// <summary>±1 step, so a code is valid for at most 90 seconds. Wider windows
    /// multiply an attacker's guessing surface for no real usability gain.</summary>
    private const int DriftSteps = 1;

    private readonly IDataProtector _protector =
        protectionProvider.CreateProtector("CalisBakalimEnik.Totp.v1");

    /// <summary>160-bit secret, as RFC 4226 recommends.</summary>
    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(20);

    public byte[] Protect(byte[] secret) => _protector.Protect(secret);

    public byte[] Unprotect(byte[] encrypted) => _protector.Unprotect(encrypted);

    /// <summary>
    /// The otpauth:// URI an authenticator app scans. The secret appears here
    /// because it has to — this string is shown once, over TLS, to the enrolling
    /// user, and must never be logged.
    /// </summary>
    public static string BuildUri(string issuer, string account, byte[] secret)
    {
        var encodedIssuer = Uri.EscapeDataString(issuer);
        var encodedAccount = Uri.EscapeDataString(account);

        return $"otpauth://totp/{encodedIssuer}:{encodedAccount}" +
               $"?secret={Base32.Encode(secret)}" +
               $"&issuer={encodedIssuer}" +
               $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    /// <summary>
    /// Verifies a code and returns the time step it matched, so the caller can
    /// record that step as spent. Returns null when no step in the window matches.
    /// </summary>
    public long? Verify(byte[] secret, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var trimmed = code.Trim().Replace(" ", string.Empty);
        if (trimmed.Length != Digits || !trimmed.All(char.IsAsciiDigit)) return null;

        var current = clock.UtcNow.ToUnixTimeSeconds() / StepSeconds;

        for (var offset = -DriftSteps; offset <= DriftSteps; offset++)
        {
            var step = current + offset;
            var expected = Compute(secret, step);

            // Constant-time: a plain == on a 6-digit code is a timing oracle.
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected),
                    Encoding.ASCII.GetBytes(trimmed)))
            {
                return step;
            }
        }

        return null;
    }

    private static string Compute(byte[] secret, long step)
    {
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);

        var hash = HMACSHA1.HashData(secret, counter);

        // Dynamic truncation, RFC 4226 §5.4.
        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        return (binary % 1_000_000).ToString("D6");
    }
}

/// <summary>
/// Base32 (RFC 4648, no padding) — what authenticator apps expect in an
/// otpauth:// secret. .NET has no built-in encoder for it.
/// </summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(byte[] data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                output.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
            output.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);

        return output.ToString();
    }
}
