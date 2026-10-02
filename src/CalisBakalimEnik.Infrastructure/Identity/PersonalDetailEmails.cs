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
            "de" => new("Bestätige deine E-Mail-Adresse",
                $"""
                 Hallo {AccountEmails.Greeting(name, "de")},

                 Du möchtest die E-Mail-Adresse deines Kontos bei Çalış Bakalım Enik in
                 diese ändern. Öffne den Link, um die Änderung abzuschließen:

                 {link}

                 Der Link gilt 24 Stunden. Wenn du das nicht angefordert hast, kannst du
                 diese E-Mail ignorieren; die Adresse bleibt, wie sie ist.

                 Çalış Bakalım Enik
                 """),
            "ru" => new("Подтверди адрес эл. почты",
                $"""
                 Привет, {AccountEmails.Greeting(name, "ru")}!

                 Ты хочешь сменить адрес эл. почты аккаунта Çalış Bakalım Enik на этот.
                 Открой ссылку, чтобы завершить смену:

                 {link}

                 Ссылка действует 24 часа. Если ты этого не запрашивал(а), просто
                 проигнорируй письмо; адрес останется прежним.

                 Çalış Bakalım Enik
                 """),
            "ar" => new("أكّد بريدك الإلكتروني",
                $"""
                 مرحبًا {AccountEmails.Greeting(name, "ar")}،

                 طلبت تغيير البريد الإلكتروني لحسابك في Çalış Bakalım Enik إلى هذا
                 العنوان. افتح الرابط لإتمام التغيير:

                 {link}

                 الرابط صالح لمدة 24 ساعة. إذا لم تطلب ذلك فتجاهل هذه الرسالة؛ يبقى
                 العنوان كما هو.

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
            "de" => new("Çalış Bakalım Enik: Änderung der E-Mail-Adresse",
                """
                 Hallo,

                 Jemand wollte die E-Mail-Adresse eines Kontos bei Çalış Bakalım Enik in
                 diese ändern, aber mit dieser Adresse gibt es schon ein Konto. Deshalb
                 wurde nichts geändert.

                 Wenn du das nicht warst, musst du nichts tun.

                 Çalış Bakalım Enik
                 """),
            "ru" => new("Çalış Bakalım Enik: смена адреса эл. почты",
                """
                 Здравствуй!

                 Кто-то хотел сменить адрес эл. почты аккаунта Çalış Bakalım Enik на
                 этот, но с этим адресом уже есть аккаунт. Поэтому ничего не изменилось.

                 Если это был(а) не ты, ничего делать не нужно.

                 Çalış Bakalım Enik
                 """),
            "ar" => new("Çalış Bakalım Enik: تغيير البريد الإلكتروني",
                """
                 مرحبًا،

                 طلب أحدهم تغيير البريد الإلكتروني لحساب في Çalış Bakalım Enik إلى هذا
                 العنوان، لكن لهذا العنوان حسابًا بالفعل. لذلك لم يتغير شيء.

                 إذا لم تطلب ذلك فلا داعي لفعل أي شيء.

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
            "de" => new("Änderung der E-Mail-Adresse angefordert",
                $"""
                 Hallo {AccountEmails.Greeting(name, "de")},

                 Wir haben eine Anfrage erhalten, die E-Mail-Adresse deines Kontos in
                 {newEmail} zu ändern. Die Adresse ändert sich, sobald der Link geöffnet
                 wird, den wir an die neue Adresse geschickt haben.

                 Wenn die Anfrage nicht von dir kam, melde dich sofort in der App an
                 und ändere dein Passwort.

                 Çalış Bakalım Enik
                 """),
            "ru" => new("Запрос на смену адреса эл. почты",
                $"""
                 Привет, {AccountEmails.Greeting(name, "ru")}!

                 Мы получили запрос сменить адрес эл. почты твоего аккаунта на
                 {newEmail}. Адрес изменится, когда откроют ссылку, которую мы
                 отправили на новый адрес.

                 Если это был(а) не ты, сразу войди в приложение и смени пароль.

                 Çalış Bakalım Enik
                 """),
            "ar" => new("طلب تغيير البريد الإلكتروني",
                $"""
                 مرحبًا {AccountEmails.Greeting(name, "ar")}،

                 تلقّينا طلبًا لتغيير البريد الإلكتروني لحسابك إلى {newEmail}. يتغير
                 العنوان عند فتح الرابط الذي أرسلناه إلى العنوان الجديد.

                 إذا لم تقدّم هذا الطلب فسجّل الدخول إلى التطبيق فورًا وغيّر كلمة
                 المرور.

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
            "de" => new("Deine E-Mail-Adresse wurde geändert",
                $"""
                 Hallo {AccountEmails.Greeting(name, "de")},

                 Die E-Mail-Adresse deines Kontos wurde in {newEmail} geändert. Ab jetzt
                 meldest du dich mit dieser Adresse an.

                 Wenn du diese Änderung nicht vorgenommen hast, antworte auf diese
                 E-Mail. Deine alte Adresse wird 12 Monate aufbewahrt; in dieser Zeit
                 können wir dir dein Konto zurückgeben.

                 Çalış Bakalım Enik
                 """),
            "ru" => new("Адрес эл. почты изменён",
                $"""
                 Привет, {AccountEmails.Greeting(name, "ru")}!

                 Адрес эл. почты твоего аккаунта изменён на {newEmail}. Теперь ты
                 входишь с этим адресом.

                 Если это сделал(а) не ты, ответь на это письмо. Старый адрес хранится
                 12 месяцев, и за это время мы можем вернуть тебе аккаунт.

                 Çalış Bakalım Enik
                 """),
            "ar" => new("تغيّر بريدك الإلكتروني",
                $"""
                 مرحبًا {AccountEmails.Greeting(name, "ar")}،

                 تغيّر البريد الإلكتروني لحسابك إلى {newEmail}. من الآن ستسجّل الدخول
                 بهذا العنوان.

                 إذا لم تُجرِ هذا التغيير فردّ على هذه الرسالة. يُحفظ عنوانك القديم
                 لمدة 12 شهرًا، ويمكننا خلالها إعادة حسابك إليك.

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
