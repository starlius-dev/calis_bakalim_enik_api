namespace CalisBakalimEnik.Infrastructure.Identity;

/// <summary>
/// The three mails of the account deletion flow (D15). Plain text, like every
/// other mail the service sends, and no dash characters (D20).
/// </summary>
public static class AccountEmails
{
    public static string Requested(string name, string when) =>
        $"""
         Merhaba {Greeting(name)},

         Çalış Bakalım Enik hesabını silme isteğini aldık. Hesabın ve tüm
         verilerin {when} tarihinde kalıcı olarak silinecek.

         Vazgeçtiysen ya da bu isteği sen yapmadıysan, o tarihe kadar uygulamaya
         giriş yapıp "Silmeyi iptal et" demen yeterli. Bu isteği sen yapmadıysan
         şifreni de değiştir.

         Çalış Bakalım Enik
         """;

    public static string Cancelled(string name) =>
        $"""
         Merhaba {Greeting(name)},

         Hesap silme isteğin iptal edildi. Hesabın ve verilerin olduğu gibi
         duruyor.

         Bu işlemi sen yapmadıysan hemen şifreni değiştir.

         Çalış Bakalım Enik
         """;

    public static string Erased(string name) =>
        $"""
         Merhaba {Greeting(name)},

         İsteğin üzerine Çalış Bakalım Enik hesabın ve tüm verilerin kalıcı olarak
         silindi. Güvenlik kayıtlarından adın ve e-posta adresin çıkarıldı; bu
         kayıtlar altı ay sonra tamamen silinecek.

         Bu, bu adrese göndereceğimiz son e-posta.

         Çalış Bakalım Enik
         """;

    private static string Greeting(string name) =>
        string.IsNullOrWhiteSpace(name) ? "merhaba" : name.Trim();
}
