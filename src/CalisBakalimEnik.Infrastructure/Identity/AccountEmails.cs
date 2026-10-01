using CalisBakalimEnik.Application.Common.Localization;

namespace CalisBakalimEnik.Infrastructure.Identity;

/// <summary>A mail's subject and body, in one language.</summary>
public sealed record MailText(string Subject, string Body);

/// <summary>
/// The three mails of the account deletion flow (D15), in the account's
/// language (J80). Plain text, like every other mail the service sends, and
/// no dash characters (D20).
/// </summary>
public static class AccountEmails
{
    public static MailText Requested(string language, string name, string when) =>
        Texts.Language(language) switch
        {
            "en" => new("Your account will be deleted",
                $"""
                 Hello {Greeting(name, "en")},

                 We received your request to delete your Çalış Bakalım Enik account.
                 Your account and all your data will be permanently deleted on {when}.

                 If you changed your mind, or did not make this request, sign in to
                 the app before then and choose "Cancel deletion". If you did not make
                 the request, change your password too.

                 Çalış Bakalım Enik
                 """),
            _ => new("Hesabın silinecek",
                $"""
                 Merhaba {Greeting(name, "tr")},

                 Çalış Bakalım Enik hesabını silme isteğini aldık. Hesabın ve tüm
                 verilerin {when} tarihinde kalıcı olarak silinecek.

                 Vazgeçtiysen ya da bu isteği sen yapmadıysan, o tarihe kadar uygulamaya
                 giriş yapıp "Silmeyi iptal et" demen yeterli. Bu isteği sen yapmadıysan
                 şifreni de değiştir.

                 Çalış Bakalım Enik
                 """),
        };

    public static MailText Cancelled(string language, string name) =>
        Texts.Language(language) switch
        {
            "en" => new("Account deletion cancelled",
                $"""
                 Hello {Greeting(name, "en")},

                 Your request to delete your account was cancelled. Your account and
                 your data are just as they were.

                 If you did not do this, change your password straight away.

                 Çalış Bakalım Enik
                 """),
            _ => new("Hesap silme iptal edildi",
                $"""
                 Merhaba {Greeting(name, "tr")},

                 Hesap silme isteğin iptal edildi. Hesabın ve verilerin olduğu gibi
                 duruyor.

                 Bu işlemi sen yapmadıysan hemen şifreni değiştir.

                 Çalış Bakalım Enik
                 """),
        };

    public static MailText Erased(string language, string name) =>
        Texts.Language(language) switch
        {
            "en" => new("Your account was deleted",
                $"""
                 Hello {Greeting(name, "en")},

                 As you asked, your Çalış Bakalım Enik account and all your data were
                 permanently deleted. Your name and e-mail address were removed from
                 the security records; those records are deleted completely in six
                 months.

                 This is the last e-mail we will send to this address.

                 Çalış Bakalım Enik
                 """),
            _ => new("Hesabın silindi",
                $"""
                 Merhaba {Greeting(name, "tr")},

                 İsteğin üzerine Çalış Bakalım Enik hesabın ve tüm verilerin kalıcı olarak
                 silindi. Güvenlik kayıtlarından adın ve e-posta adresin çıkarıldı; bu
                 kayıtlar altı ay sonra tamamen silinecek.

                 Bu, bu adrese göndereceğimiz son e-posta.

                 Çalış Bakalım Enik
                 """),
        };

    internal static string Greeting(string name, string language) =>
        string.IsNullOrWhiteSpace(name) ? (language == "en" ? "there" : "merhaba") : name.Trim();
}
