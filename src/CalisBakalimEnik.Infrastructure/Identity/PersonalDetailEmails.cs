namespace CalisBakalimEnik.Infrastructure.Identity;

/// <summary>
/// The mails of an e-mail address change (J78). Plain text and no dash
/// characters, like the rest (D20).
/// </summary>
/// <remarks>
/// The OLD address hears about both the request and the change. If someone
/// else has the session, that mail is how the owner finds out while it can
/// still be undone.
/// </remarks>
public static class PersonalDetailEmails
{
    /// <summary>To the new address: the link that makes the change.</summary>
    public static string Confirm(string name, string link) =>
        $"""
         Merhaba {Greeting(name)},

         Çalış Bakalım Enik hesabının e-posta adresini bu adresle değiştirmek
         istedin. Değişikliği tamamlamak için bağlantıyı aç:

         {link}

         Bağlantı 24 saat geçerli. Bu isteği sen yapmadıysan bu e-postayı yok
         sayabilirsin; adres değişmez.

         Çalış Bakalım Enik
         """;

    /// <summary>
    /// To the new address when it already has an account. Said there, not in
    /// the app, so the app cannot be used to test which addresses exist.
    /// </summary>
    public static string AlreadyUsed() =>
        """
         Merhaba,

         Bir Çalış Bakalım Enik hesabının e-posta adresini bu adresle değiştirmek
         istendi, ama bu adresle zaten bir hesap var. Bu yüzden değişiklik
         yapılmadı.

         Bu isteği sen yapmadıysan bir şey yapmana gerek yok.

         Çalış Bakalım Enik
         """;

    /// <summary>To the old address, when the change is asked for.</summary>
    public static string Requested(string name, string newEmail) =>
        $"""
         Merhaba {Greeting(name)},

         Hesabının e-posta adresini {newEmail} olarak değiştirme isteği aldık.
         Adres, yeni adrese gönderdiğimiz bağlantı açılınca değişecek.

         Bu isteği sen yapmadıysan hemen uygulamaya girip şifreni değiştir.

         Çalış Bakalım Enik
         """;

    /// <summary>To the old address, once the change is made.</summary>
    public static string Changed(string name, string newEmail) =>
        $"""
         Merhaba {Greeting(name)},

         Hesabının e-posta adresi {newEmail} olarak değiştirildi. Bundan sonra
         giriş için bu adresi kullanacaksın.

         Bu değişikliği sen yapmadıysan bu e-postayı yanıtla. Eski adresin 12 ay
         boyunca saklanıyor, bu sürede hesabını geri alabiliriz.

         Çalış Bakalım Enik
         """;

    private static string Greeting(string name) =>
        string.IsNullOrWhiteSpace(name) ? "merhaba" : name.Trim();
}
