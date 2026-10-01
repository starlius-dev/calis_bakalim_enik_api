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
            "de" => new("Dein Konto wird gelöscht",
                $"""
                 Hallo {Greeting(name, "de")},

                 Wir haben deine Anfrage erhalten, dein Konto bei Çalış Bakalım Enik zu
                 löschen. Dein Konto und alle deine Daten werden am {when} endgültig
                 gelöscht.

                 Wenn du es dir anders überlegt hast oder die Anfrage nicht von dir
                 kam, melde dich vorher in der App an und wähle "Löschung widerrufen".
                 Kam die Anfrage nicht von dir, ändere außerdem dein Passwort.

                 Çalış Bakalım Enik
                 """),
            "ru" => new("Твой аккаунт будет удалён",
                $"""
                 Привет, {Greeting(name, "ru")}!

                 Мы получили запрос на удаление твоего аккаунта Çalış Bakalım Enik.
                 Аккаунт и все данные будут удалены навсегда {when}.

                 Если ты передумал(а) или не отправлял(а) этот запрос, войди в
                 приложение до этого срока и выбери «Отменить удаление». Если запрос
                 отправлял(а) не ты, смени также пароль.

                 Çalış Bakalım Enik
                 """),
            "ar" => new("سيُحذف حسابك",
                $"""
                 مرحبًا {Greeting(name, "ar")}،

                 تلقّينا طلبك لحذف حسابك في Çalış Bakalım Enik. سيُحذف حسابك وجميع
                 بياناتك نهائيًا في {when}.

                 إذا غيّرت رأيك أو لم تقدّم هذا الطلب، فسجّل الدخول إلى التطبيق قبل
                 ذلك واختر "إلغاء الحذف". وإذا لم تقدّم الطلب فغيّر كلمة المرور أيضًا.

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
            "de" => new("Kontolöschung widerrufen",
                $"""
                 Hallo {Greeting(name, "de")},

                 Deine Anfrage zur Löschung deines Kontos wurde widerrufen. Dein Konto
                 und deine Daten sind unverändert.

                 Wenn du das nicht warst, ändere sofort dein Passwort.

                 Çalış Bakalım Enik
                 """),
            "ru" => new("Удаление аккаунта отменено",
                $"""
                 Привет, {Greeting(name, "ru")}!

                 Запрос на удаление аккаунта отменён. Аккаунт и данные остались без
                 изменений.

                 Если это сделал(а) не ты, сразу смени пароль.

                 Çalış Bakalım Enik
                 """),
            "ar" => new("أُلغي حذف الحساب",
                $"""
                 مرحبًا {Greeting(name, "ar")}،

                 أُلغي طلب حذف حسابك. حسابك وبياناتك كما هي.

                 إذا لم تفعل ذلك بنفسك فغيّر كلمة المرور فورًا.

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
            "de" => new("Dein Konto wurde gelöscht",
                $"""
                 Hallo {Greeting(name, "de")},

                 Wie gewünscht wurden dein Konto bei Çalış Bakalım Enik und alle deine
                 Daten endgültig gelöscht. Dein Name und deine E-Mail-Adresse wurden aus
                 den Sicherheitsprotokollen entfernt; diese Protokolle werden in sechs
                 Monaten vollständig gelöscht.

                 Das ist die letzte E-Mail, die wir an diese Adresse schicken.

                 Çalış Bakalım Enik
                 """),
            "ru" => new("Твой аккаунт удалён",
                $"""
                 Привет, {Greeting(name, "ru")}!

                 По твоей просьбе аккаунт Çalış Bakalım Enik и все данные удалены
                 навсегда. Имя и адрес эл. почты удалены из журналов безопасности;
                 сами журналы будут полностью удалены через шесть месяцев.

                 Это последнее письмо, которое мы отправляем на этот адрес.

                 Çalış Bakalım Enik
                 """),
            "ar" => new("حُذف حسابك",
                $"""
                 مرحبًا {Greeting(name, "ar")}،

                 كما طلبت، حُذف حسابك في Çalış Bakalım Enik وجميع بياناتك نهائيًا.
                 أُزيل اسمك وبريدك الإلكتروني من سجلات الأمان، وستُحذف هذه السجلات
                 بالكامل بعد ستة أشهر.

                 هذه آخر رسالة نرسلها إلى هذا العنوان.

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
        string.IsNullOrWhiteSpace(name)
            ? language switch { "en" => "there", "de" => "du", "ru" => "друг", "ar" => "صديقي", _ => "merhaba" }
            : name.Trim();
}
