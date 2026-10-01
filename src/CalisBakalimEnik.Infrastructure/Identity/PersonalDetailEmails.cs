using CalisBakalimEnik.Application.Common.Localization;

namespace CalisBakalimEnik.Infrastructure.Identity;

/// <summary>
/// The mails of an e-mail address change (J78), in the account's language
/// (J80). Plain text and no dash characters, like the rest (D20).
/// </summary>
/// <remarks>
/// The OLD address hears about both the request and the change. If someone
/// else has the session, that mail is how the owner finds out while it can
/// still be undone.
/// </remarks>
public static class PersonalDetailEmails
{
    /// <summary>To the new address: the link that makes the change.</summary>
    public static MailText Confirm(string language, string name, string link) =>
        Texts.Language(language) switch
        {
            "en" => new("Confirm your e-mail address",
                $"""
                 Hello {AccountEmails.Greeting(name, "en")},

                 You asked to change your Çalış Bakalım Enik account's e-mail address
                 to this one. Open the link to finish the change:

                 {link}

                 The link works for 24 hours. If you did not ask for this, you can
                 ignore this e-mail; the address stays as it is.

                 Çalış Bakalım Enik
                 """),
            _ => new("E-posta adresini onayla",
                $"""
                 Merhaba {AccountEmails.Greeting(name, "tr")},

                 Çalış Bakalım Enik hesabının e-posta adresini bu adresle değiştirmek
                 istedin. Değişikliği tamamlamak için bağlantıyı aç:

                 {link}

                 Bağlantı 24 saat geçerli. Bu isteği sen yapmadıysan bu e-postayı yok
                 sayabilirsin; adres değişmez.

                 Çalış Bakalım Enik
                 """),
        };

    /// <summary>
    /// To the new address when it already has an account. Said there, not in
    /// the app, so the app cannot be used to test which addresses exist.
    /// </summary>
    public static MailText AlreadyUsed(string language) =>
        Texts.Language(language) switch
        {
            "en" => new("Çalış Bakalım Enik: e-mail change",
                """
                 Hello,

                 Someone asked to change a Çalış Bakalım Enik account's e-mail address
                 to this one, but this address already has an account. So nothing was
                 changed.

                 If you did not ask for this, there is nothing you need to do.

                 Çalış Bakalım Enik
                 """),
            _ => new("Çalış Bakalım Enik: e-posta değişikliği",
                """
                 Merhaba,

                 Bir Çalış Bakalım Enik hesabının e-posta adresini bu adresle değiştirmek
                 istendi, ama bu adresle zaten bir hesap var. Bu yüzden değişiklik
                 yapılmadı.

                 Bu isteği sen yapmadıysan bir şey yapmana gerek yok.

                 Çalış Bakalım Enik
                 """),
        };

    /// <summary>To the old address, when the change is asked for.</summary>
    public static MailText Requested(string language, string name, string newEmail) =>
        Texts.Language(language) switch
        {
            "en" => new("E-mail change requested",
                $"""
                 Hello {AccountEmails.Greeting(name, "en")},

                 We received a request to change your account's e-mail address to
                 {newEmail}. The address changes when the link we sent to the new
                 address is opened.

                 If you did not make this request, sign in to the app straight away
                 and change your password.

                 Çalış Bakalım Enik
                 """),
            _ => new("E-posta değişikliği isteği",
                $"""
                 Merhaba {AccountEmails.Greeting(name, "tr")},

                 Hesabının e-posta adresini {newEmail} olarak değiştirme isteği aldık.
                 Adres, yeni adrese gönderdiğimiz bağlantı açılınca değişecek.

                 Bu isteği sen yapmadıysan hemen uygulamaya girip şifreni değiştir.

                 Çalış Bakalım Enik
                 """),
        };

    /// <summary>To the old address, once the change is made.</summary>
    public static MailText Changed(string language, string name, string newEmail) =>
        Texts.Language(language) switch
        {
            "en" => new("Your e-mail address changed",
                $"""
                 Hello {AccountEmails.Greeting(name, "en")},

                 Your account's e-mail address was changed to {newEmail}. From now on
                 you sign in with that address.

                 If you did not make this change, reply to this e-mail. Your old
                 address is kept for 12 months, and in that time we can give you your
                 account back.

                 Çalış Bakalım Enik
                 """),
            _ => new("E-posta adresin değişti",
                $"""
                 Merhaba {AccountEmails.Greeting(name, "tr")},

                 Hesabının e-posta adresi {newEmail} olarak değiştirildi. Bundan sonra
                 giriş için bu adresi kullanacaksın.

                 Bu değişikliği sen yapmadıysan bu e-postayı yanıtla. Eski adresin 12 ay
                 boyunca saklanıyor, bu sürede hesabını geri alabiliriz.

                 Çalış Bakalım Enik
                 """),
        };
}
