using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Application.Common.Models;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Infrastructure.Identity;

public sealed record FactorSummary(
    Guid Id,
    MfaFactorType Type,
    bool IsPrimary,
    bool Verified,
    string? MaskedDestination,
    int? RemainingCodes);

public sealed record TotpEnrolment(Guid FactorId, string Secret, string OtpAuthUri);

public sealed class MfaService(
    AppDbContext db,
    TotpService totp,
    MfaChallengeStore challenges,
    IEmailSender email,
    IClock clock)
{
    private const string Issuer = "Calis Bakalim Enik";

    /// <summary>
    /// The factors a user may actually be challenged on.
    /// </summary>
    /// <remarks>
    /// SMS rows written before the factor was removed are excluded here rather
    /// than left to fail later. Nothing delivers an SMS code any more, so an
    /// offered SMS factor would produce a challenge that can never be
    /// satisfied — and if it were the primary one, that is a lockout.
    /// </remarks>
    public async Task<IReadOnlyList<MfaFactor>> GetUsableFactorsAsync(
        Guid userId, CancellationToken ct) =>
        await db.MfaFactors
            .Where(f => f.UserId == userId
                        && f.DeletedAt == null
                        && f.VerifiedAt != null
#pragma warning disable CS0618 // Reserved value, referenced here on purpose.
                        && f.FactorType != MfaFactorType.SmsOtp)
#pragma warning restore CS0618
            .OrderByDescending(f => f.IsPrimary)
            .ThenBy(f => f.FactorType)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<FactorSummary>> ListAsync(Guid userId, CancellationToken ct)
    {
        var factors = await db.MfaFactors
            .Where(f => f.UserId == userId && f.DeletedAt == null)
            .OrderByDescending(f => f.IsPrimary)
            .ThenBy(f => f.FactorType)
            .ToListAsync(ct);

        var remaining = await CountRemainingCodesAsync(userId, ct);

        return factors.Select(f => new FactorSummary(
            f.Id,
            f.FactorType,
            f.IsPrimary,
            f.VerifiedAt is not null,
            f.MaskedDestination,
            f.FactorType == MfaFactorType.RecoveryCode ? remaining : null)).ToList();
    }

    public Task<int> CountRemainingCodesAsync(Guid userId, CancellationToken ct) =>
        db.MfaRecoveryCodes.CountAsync(c => c.UserId == userId && c.UsedAt == null, ct);

    // ── enrolment ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an UNVERIFIED TOTP factor and returns the secret once. The factor
    /// cannot be used until <see cref="ConfirmTotpAsync"/> proves the user's app
    /// generates matching codes — otherwise a bad scan locks them out.
    /// </summary>
    public async Task<TotpEnrolment> BeginTotpEnrolmentAsync(
        Guid userId, string account, CancellationToken ct)
    {
        await SoftDeleteExistingAsync(userId, MfaFactorType.Totp, ct);

        var secret = TotpService.GenerateSecret();

        var factor = new MfaFactor
        {
            UserId = userId,
            FactorType = MfaFactorType.Totp,
            SecretEncrypted = totp.Protect(secret),
            CreatedAt = clock.UtcNow,
        };

        db.MfaFactors.Add(factor);
        await db.SaveChangesAsync(ct);

        return new TotpEnrolment(
            factor.Id,
            Base32.Encode(secret),
            TotpService.BuildUri(Issuer, account, secret));
    }

    public async Task<Result> ConfirmTotpAsync(Guid userId, string code, CancellationToken ct)
    {
        var factor = await db.MfaFactors.FirstOrDefaultAsync(
            f => f.UserId == userId
                 && f.FactorType == MfaFactorType.Totp
                 && f.DeletedAt == null, ct);

        if (factor?.SecretEncrypted is null)
            return Result.Failure(MfaErrors.NotEnrolled);

        var step = totp.Verify(totp.Unprotect(factor.SecretEncrypted), code);
        if (step is null) return Result.Failure(MfaErrors.InvalidCode);

        factor.VerifiedAt = clock.UtcNow;
        factor.LastUsedAt = clock.UtcNow;
        await EnsurePrimaryAsync(factor, ct);
        await db.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<Guid> BeginOtpEnrolmentAsync(
        Guid userId, MfaFactorType type, string destination, CancellationToken ct)
    {
        await SoftDeleteExistingAsync(userId, type, ct);

        var factor = new MfaFactor
        {
            UserId = userId,
            FactorType = type,
            Destination = destination,
            CreatedAt = clock.UtcNow,
        };

        db.MfaFactors.Add(factor);
        await db.SaveChangesAsync(ct);

        return factor.Id;
    }

    public async Task<Result> ConfirmOtpAsync(
        Guid userId, Guid challengeId, string code, CancellationToken ct)
    {
        var verified = await VerifyChallengeAsync(challengeId, code, ct);
        if (verified.Failed) return Result.Failure(verified.Error);

        var factor = await db.MfaFactors.FirstOrDefaultAsync(
            f => f.Id == verified.Value.FactorId && f.UserId == userId, ct);

        if (factor is null) return Result.Failure(MfaErrors.NotEnrolled);

        factor.VerifiedAt = clock.UtcNow;
        await EnsurePrimaryAsync(factor, ct);
        await db.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Refuses to remove the last verified factor while MFA is required, so a
    /// user cannot accidentally strip their own protection and get stuck.
    /// </summary>
    public async Task<Result> RemoveFactorAsync(
        Guid userId, Guid factorId, bool mfaRequired, CancellationToken ct)
    {
        var factor = await db.MfaFactors.FirstOrDefaultAsync(
            f => f.Id == factorId && f.UserId == userId && f.DeletedAt == null, ct);

        if (factor is null) return Result.Failure(MfaErrors.NotEnrolled);

        var remaining = await db.MfaFactors.CountAsync(
            f => f.UserId == userId
                 && f.DeletedAt == null
                 && f.VerifiedAt != null
                 && f.Id != factorId, ct);

        if (remaining == 0 && mfaRequired)
            return Result.Failure(MfaErrors.LastFactor);

        factor.DeletedAt = clock.UtcNow;
        factor.IsPrimary = false;
        await db.SaveChangesAsync(ct);

        if (remaining > 0 && !await db.MfaFactors.AnyAsync(
                f => f.UserId == userId && f.IsPrimary && f.DeletedAt == null, ct))
        {
            var next = await db.MfaFactors.FirstAsync(
                f => f.UserId == userId && f.DeletedAt == null && f.VerifiedAt != null, ct);
            next.IsPrimary = true;
            await db.SaveChangesAsync(ct);
        }

        return Result.Success();
    }

    // ── recovery codes ───────────────────────────────────────────────────

    /// <summary>Returns the plaintext codes ONCE. Regenerating invalidates the old batch.</summary>
    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(
        Guid userId, CancellationToken ct)
    {
        await db.MfaRecoveryCodes
            .Where(c => c.UserId == userId)
            .ExecuteDeleteAsync(ct);

        var codes = Enumerable.Range(0, RecoveryCodeService.BatchSize)
            .Select(_ => RecoveryCodeService.Generate())
            .ToList();

        db.MfaRecoveryCodes.AddRange(codes.Select(c => new MfaRecoveryCode
        {
            UserId = userId,
            CodeHash = RecoveryCodeService.Hash(c),
            CreatedAt = clock.UtcNow,
        }));

        if (!await db.MfaFactors.AnyAsync(
                f => f.UserId == userId
                     && f.FactorType == MfaFactorType.RecoveryCode
                     && f.DeletedAt == null, ct))
        {
            db.MfaFactors.Add(new MfaFactor
            {
                UserId = userId,
                FactorType = MfaFactorType.RecoveryCode,
                VerifiedAt = clock.UtcNow,
                CreatedAt = clock.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);
        return codes;
    }

    // ── challenge / verification ─────────────────────────────────────────

    /// <summary>Creates a challenge and, for OTP factors, delivers the code.</summary>
    public async Task<Guid> StartChallengeAsync(
        Guid userId, MfaFactor factor, CancellationToken ct)
    {
        string? code = null;

        if (factor.FactorType is MfaFactorType.EmailOtp)
        {
            code = MfaChallengeStore.GenerateNumericCode();

            if (await challenges.TryStartResendCooldownAsync(userId, factor.FactorType, ct))
                await DeliverAsync(factor, code, ct);
        }

        return await challenges.CreateAsync(userId, factor.Id, factor.FactorType, code, ct);
    }

    public async Task<Result<MfaChallenge>> VerifyChallengeAsync(
        Guid challengeId, string code, CancellationToken ct)
    {
        var challenge = await challenges.GetAsync(challengeId, ct);
        if (challenge is null) return Result.Failure<MfaChallenge>(MfaErrors.ChallengeExpired);

        var ok = challenge.FactorType switch
        {
            MfaFactorType.Totp => await VerifyTotpAsync(challenge, code, ct),
            MfaFactorType.RecoveryCode => await VerifyRecoveryCodeAsync(challenge, code, ct),
            _ => MfaChallengeStore.CodeMatches(challenge, code),
        };

        if (!ok)
        {
            var attempts = await challenges.RecordAttemptAsync(challengeId, challenge, ct);

            return Result.Failure<MfaChallenge>(
                attempts >= MfaChallengeStore.MaxAttempts
                    ? MfaErrors.AttemptsExhausted
                    : MfaErrors.InvalidCode);
        }

        await challenges.ConsumeAsync(challengeId, ct);

        var factor = await db.MfaFactors.FirstOrDefaultAsync(f => f.Id == challenge.FactorId, ct);
        if (factor is not null)
        {
            factor.LastUsedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return Result.Success(challenge);
    }

    private async Task<bool> VerifyTotpAsync(
        MfaChallenge challenge, string code, CancellationToken ct)
    {
        var factor = await db.MfaFactors.FirstOrDefaultAsync(f => f.Id == challenge.FactorId, ct);
        if (factor?.SecretEncrypted is null) return false;

        var step = totp.Verify(totp.Unprotect(factor.SecretEncrypted), code);
        if (step is null) return false;

        // A code sniffed in transit must not work twice inside its own window.
        return await challenges.TryConsumeTotpStepAsync(factor.Id, step.Value, ct);
    }

    private async Task<bool> VerifyRecoveryCodeAsync(
        MfaChallenge challenge, string code, CancellationToken ct)
    {
        var candidates = await db.MfaRecoveryCodes
            .Where(c => c.UserId == challenge.UserId && c.UsedAt == null)
            .ToListAsync(ct);

        var match = candidates.FirstOrDefault(c => RecoveryCodeService.Verify(code, c.CodeHash));
        if (match is null) return false;

        match.UsedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task DeliverAsync(MfaFactor factor, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(factor.Destination)) return;

        var body = $"Çalış Bakalım Enik doğrulama kodun: {code}\n" +
                   $"Kod {MfaChallengeStore.Lifetime.TotalMinutes:0} dakika geçerli.";

        // Email is the only delivered factor. SMS was removed: it is the weakest
        // second factor (SIM-swap prone), it costs money per send, and TOTP plus
        // recovery codes already cover the same ground better.
        if (factor.FactorType == MfaFactorType.EmailOtp)
            await email.SendAsync(factor.Destination, "Doğrulama kodun", body, ct);
    }

    private async Task SoftDeleteExistingAsync(
        Guid userId, MfaFactorType type, CancellationToken ct)
    {
        await db.MfaFactors
            .Where(f => f.UserId == userId && f.FactorType == type && f.DeletedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(f => f.DeletedAt, clock.UtcNow)
                      .SetProperty(f => f.IsPrimary, false),
                ct);
    }

    private async Task EnsurePrimaryAsync(MfaFactor factor, CancellationToken ct)
    {
        if (factor.IsPrimary) return;

        var hasPrimary = await db.MfaFactors.AnyAsync(
            f => f.UserId == factor.UserId
                 && f.IsPrimary
                 && f.DeletedAt == null
                 && f.Id != factor.Id, ct);

        if (!hasPrimary) factor.IsPrimary = true;
    }
}

public static class MfaErrors
{
    public static readonly Error NotEnrolled =
        new("mfa.not_enrolled", "Bu doğrulama yöntemi kayıtlı değil.");

    public static readonly Error InvalidCode =
        new("mfa.invalid_code", "Kod hatalı.");

    public static readonly Error ChallengeExpired =
        new("mfa.challenge_expired", "Doğrulama süresi doldu. Tekrar giriş yap.");

    public static readonly Error AttemptsExhausted =
        new("mfa.attempts_exhausted", "Çok fazla hatalı deneme. Tekrar giriş yap.");

    public static readonly Error LastFactor =
        new("mfa.last_factor", "Son doğrulama yöntemini kaldıramazsın.");

    public static readonly Error ResendTooSoon =
        new("mfa.resend_too_soon", "Yeni kod istemek için biraz bekle.");
}
