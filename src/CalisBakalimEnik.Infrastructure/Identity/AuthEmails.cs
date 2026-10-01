using CalisBakalimEnik.Application.Common.Localization;

namespace CalisBakalimEnik.Infrastructure.Identity;

/// <summary>
/// Sign-up, password reset and sign-in code mails, in the account's language
/// (J80). They used to be written inline next to each endpoint.
/// </summary>
public static class AuthEmails
{
    /// <summary>To an address that already has an account, on a new sign-up.</summary>
    public static MailText RegisterAttempt(string language, string forgotLink) =>
        Texts.Language(language) switch
        {
            "en" => new("Çalış Bakalım Enik: sign-up attempt",
                $"""
                 There is already an account with this address, so we did not open a
                 new one.

                 If you don't remember your password, you can reset it here:

                 {forgotLink}
                 """),
            _ => new("Çalış Bakalım Enik: kayıt denemesi",
                $"""
                 Bu adresle zaten bir hesap var, bu yüzden yeni bir hesap açmadık.

                 Şifreni hatırlamıyorsan buradan sıfırlayabilirsin:

                 {forgotLink}
                 """),
        };

    public static MailText ConfirmEmail(string language, string name, string link) =>
        Texts.Language(language) switch
        {
            "en" => new("Çalış Bakalım Enik: confirm your e-mail",
                $"""
                 Hello {name},

                 One last step to open your account. Click the link below:

                 {link}

                 The link works for 24 hours.
                 """),
            _ => new("Çalış Bakalım Enik: e-postanı doğrula",
                $"""
                 Merhaba {name},

                 Hesabını açmak için son bir adım kaldı. Aşağıdaki bağlantıya tıkla:

                 {link}

                 Bağlantı 24 saat geçerli.
                 """),
        };

    public static MailText ResetPassword(string language, string link) =>
        Texts.Language(language) switch
        {
            "en" => new("Çalış Bakalım Enik: password reset",
                $"""
                 Click the link below to reset your password:

                 {link}

                 The link works for 30 minutes. If you did not ask for this, there is
                 nothing you need to do. Your password has not changed.
                 """),
            _ => new("Çalış Bakalım Enik: şifre sıfırlama",
                $"""
                 Şifreni sıfırlamak için aşağıdaki bağlantıya tıkla:

                 {link}

                 Bağlantı 30 dakika geçerli. Bu isteği sen yapmadıysan
                 hiçbir şey yapmana gerek yok. Şifren değişmedi.
                 """),
        };

    public static MailText SignInCode(string language, string code, int minutes) =>
        Texts.Language(language) switch
        {
            "en" => new("Your verification code",
                $"Your Çalış Bakalım Enik verification code: {code}\n" +
                $"The code works for {minutes} minutes."),
            _ => new("Doğrulama kodun",
                $"Çalış Bakalım Enik doğrulama kodun: {code}\n" +
                $"Kod {minutes} dakika geçerli."),
        };
}
