using System.Security.Cryptography;
using System.Text;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;

namespace CalisBakalimEnik.UnitTests.Identity;

public class TotpServiceTests
{
    private readonly FixedClock _clock = new();
    private readonly TotpService _totp;

    public TotpServiceTests() =>
        _totp = new TotpService(DataProtectionProvider.Create("Tests"), _clock);

    [Fact]
    public void Secrets_are_160_bit_and_never_repeat()
    {
        var secrets = Enumerable.Range(0, 50)
            .Select(_ => Convert.ToBase64String(TotpService.GenerateSecret()))
            .ToHashSet();

        secrets.Should().HaveCount(50);
        TotpService.GenerateSecret().Should().HaveCount(20, "RFC 4226 recommends 160 bits");
    }

    [Fact]
    public void A_generated_code_verifies_against_its_own_secret()
    {
        var secret = TotpService.GenerateSecret();
        var code = Reference(secret, _clock.UtcNow.ToUnixTimeSeconds() / 30);

        _totp.Verify(secret, code).Should().NotBeNull();
    }

    [Fact]
    public void Matches_a_known_RFC_6238_vector()
    {
        // RFC 6238 Appendix B, SHA-1, T = 59 -> 94287082 (8 digits).
        // Truncated to the 6 the app uses.
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        var clock = new FixedClock { UtcNow = DateTimeOffset.FromUnixTimeSeconds(59) };
        var totp = new TotpService(DataProtectionProvider.Create("Tests"), clock);

        totp.Verify(secret, "287082").Should().NotBeNull(
            "an implementation that does not match the RFC vector will not match any authenticator app");
    }

    [Fact]
    public void Accepts_one_step_of_drift_but_not_two()
    {
        var secret = TotpService.GenerateSecret();
        var step = _clock.UtcNow.ToUnixTimeSeconds() / 30;

        _totp.Verify(secret, Reference(secret, step - 1)).Should().NotBeNull();
        _totp.Verify(secret, Reference(secret, step + 1)).Should().NotBeNull();

        _totp.Verify(secret, Reference(secret, step - 2)).Should().BeNull(
            "a wider window multiplies the guessing surface for no usability gain");
        _totp.Verify(secret, Reference(secret, step + 2)).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    [InlineData("12 34 56")]
    public void Rejects_malformed_input_without_throwing(string code)
        => _totp.Verify(TotpService.GenerateSecret(), code).Should().BeNull();

    [Fact]
    public void The_secret_is_encrypted_at_rest_and_round_trips()
    {
        var secret = TotpService.GenerateSecret();
        var protectedSecret = _totp.Protect(secret);

        protectedSecret.Should().NotEqual(secret, "the stored value must not be the raw secret");
        _totp.Unprotect(protectedSecret).Should().Equal(secret);
    }

    [Fact]
    public void The_otpauth_uri_carries_what_an_authenticator_app_needs()
    {
        var uri = TotpService.BuildUri("Calis Bakalim Enik", "zeynep@ornek.com",
            TotpService.GenerateSecret());

        uri.Should().StartWith("otpauth://totp/");
        uri.Should().Contain("algorithm=SHA1").And.Contain("digits=6").And.Contain("period=30");
        uri.Should().Contain("issuer=");
    }

    /// <summary>An independent RFC 6238 implementation, so the test does not
    /// simply agree with the code it is testing.</summary>
    private static string Reference(byte[] secret, long step)
    {
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);

        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        return (binary % 1_000_000).ToString("D6");
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } =
            new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    }
}

public class RecoveryCodeServiceTests
{
    [Fact]
    public void Codes_avoid_the_glyphs_people_mistype()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => RecoveryCodeService.Generate()).ToList();

        codes.Should().OnlyContain(c => c.Length == 11 && c[5] == '-');
        codes.Should().OnlyContain(c => !c.Contains('I') && !c.Contains('L')
                                        && !c.Contains('O') && !c.Contains('U'),
            "a code is read off a screen and typed by hand");
    }

    [Fact]
    public void Codes_are_unpredictable()
        => Enumerable.Range(0, 500)
            .Select(_ => RecoveryCodeService.Generate())
            .ToHashSet()
            .Should().HaveCount(500);

    [Fact]
    public void A_code_verifies_against_its_own_hash()
    {
        var code = RecoveryCodeService.Generate();
        var hash = RecoveryCodeService.Hash(code);

        hash.Should().StartWith("$argon2id$");
        hash.Should().NotContain(code, "the hash must not embed the code");
        RecoveryCodeService.Verify(code, hash).Should().BeTrue();
    }

    [Fact]
    public void The_same_code_hashes_differently_every_time()
    {
        var code = RecoveryCodeService.Generate();

        RecoveryCodeService.Hash(code).Should().NotBe(RecoveryCodeService.Hash(code),
            "a per-code salt is what stops a precomputed table");
    }

    [Theory]
    [InlineData("abcde-fghjk")]   // lowercase
    [InlineData("ABCDEFGHJK")]    // no dash
    [InlineData(" ABCDE-FGHJK ")] // padded
    public void Verification_tolerates_how_a_human_types_it(string variant)
    {
        var hash = RecoveryCodeService.Hash("ABCDE-FGHJK");
        RecoveryCodeService.Verify(variant, hash).Should().BeTrue();
    }

    [Fact]
    public void A_wrong_code_does_not_verify()
    {
        var hash = RecoveryCodeService.Hash(RecoveryCodeService.Generate());
        RecoveryCodeService.Verify(RecoveryCodeService.Generate(), hash).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2id$broken")]
    public void Malformed_stored_hashes_fail_closed(string stored)
        => RecoveryCodeService.Verify("ABCDE-FGHJK", stored).Should().BeFalse();
}
