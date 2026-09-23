using Microsoft.AspNetCore.Identity;

namespace CalisBakalimEnik.Infrastructure.Identity;

/// <summary>
/// Identity's own validation messages, in the product's language.
/// </summary>
/// <remarks>
/// <para>The API writes its rules in Turkish everywhere it owns them —
/// <c>"Başlık boş olamaz."</c>, <c>"Geçerli bir saat dilimi seç."</c> — but the
/// rules Identity owns came back in English, and those are the ones on the
/// signup and password screens. A user who typed a short password read
/// <c>"Passwords must be at least 10 characters."</c> in an otherwise Turkish
/// form.</para>
///
/// <para>Only the messages that can actually reach a user are overridden. The
/// rest inherit, so an unexpected failure still says something rather than
/// falling through to an empty string — and so this file does not have to be
/// revisited every time the framework adds a code.</para>
///
/// <para><b>These are user-facing strings and they say nothing about who is
/// registered.</b> <c>DuplicateEmail</c> is overridden here for completeness,
/// but registration never returns it: that path answers 204 whatever happens,
/// because a different answer for an address that exists turns signup into an
/// enumeration oracle. See docs/SECURITY.md §2.</para>
/// </remarks>
public sealed class TurkishIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => new()
    {
        Code = nameof(DefaultError),
        Description = "Bilinmeyen bir hata oluştu.",
    };

    public override IdentityError PasswordTooShort(int length) => new()
    {
        Code = nameof(PasswordTooShort),
        Description = $"Şifre en az {length} karakter olmalı.",
    };

    public override IdentityError PasswordRequiresUpper() => new()
    {
        Code = nameof(PasswordRequiresUpper),
        Description = "Şifre en az bir büyük harf içermeli.",
    };

    public override IdentityError PasswordRequiresLower() => new()
    {
        Code = nameof(PasswordRequiresLower),
        Description = "Şifre en az bir küçük harf içermeli.",
    };

    public override IdentityError PasswordRequiresDigit() => new()
    {
        Code = nameof(PasswordRequiresDigit),
        Description = "Şifre en az bir rakam içermeli.",
    };

    public override IdentityError PasswordRequiresNonAlphanumeric() => new()
    {
        Code = nameof(PasswordRequiresNonAlphanumeric),
        Description = "Şifre en az bir sembol içermeli.",
    };

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => new()
    {
        Code = nameof(PasswordRequiresUniqueChars),
        Description = $"Şifre en az {uniqueChars} farklı karakter içermeli.",
    };

    public override IdentityError InvalidEmail(string? email) => new()
    {
        Code = nameof(InvalidEmail),
        // The address is deliberately NOT echoed. It adds nothing the user
        // cannot see in the field they just typed, and it keeps the address out
        // of anything that logs or screenshots the response.
        Description = "Geçerli bir e-posta adresi gir.",
    };

    public override IdentityError InvalidUserName(string? userName) => new()
    {
        Code = nameof(InvalidUserName),
        Description = "Geçerli bir e-posta adresi gir.",
    };

    public override IdentityError DuplicateEmail(string? email) => new()
    {
        Code = nameof(DuplicateEmail),
        Description = "Bu adres kullanılamıyor.",
    };

    public override IdentityError DuplicateUserName(string? userName) => new()
    {
        Code = nameof(DuplicateUserName),
        Description = "Bu adres kullanılamıyor.",
    };

    public override IdentityError PasswordMismatch() => new()
    {
        Code = nameof(PasswordMismatch),
        Description = "Şifre hatalı.",
    };

    public override IdentityError InvalidToken() => new()
    {
        Code = nameof(InvalidToken),
        Description = "Bu bağlantı geçersiz ya da süresi dolmuş.",
    };

    public override IdentityError UserAlreadyHasPassword() => new()
    {
        Code = nameof(UserAlreadyHasPassword),
        Description = "Bu hesabın zaten bir şifresi var.",
    };

    // No lockout message: Identity's describer has none, because lockout is a
    // sign-in result rather than a validation error. This API's lockout is
    // BruteForceGuard's and answers from AuthErrors instead. See SECURITY.md §5.

    public override IdentityError ConcurrencyFailure() => new()
    {
        Code = nameof(ConcurrencyFailure),
        Description = "Kayıt sen düzenlerken değişti. Tekrar dene.",
    };
}
