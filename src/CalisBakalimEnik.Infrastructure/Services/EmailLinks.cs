namespace CalisBakalimEnik.Infrastructure.Services;

/// <summary>
/// The links that go out in mail.
///
/// The web build uses hash routing, so every deep link carries <c>/#/</c>. The
/// tokens are URL-escaped: Identity's tokens are base64 and contain <c>+</c>,
/// <c>/</c> and <c>=</c>, and an unescaped <c>+</c> arrives at the client as a
/// space — the classic "the link in the email doesn't work" bug.
/// </summary>
public static class EmailLinks
{
    public static string ConfirmEmail(EmailOptions options, Guid userId, string token) =>
        $"{Root(options)}/#/kayit/dogrula?userId={userId}&token={Uri.EscapeDataString(token)}";

    public static string ResetPassword(EmailOptions options, Guid userId, string token) =>
        $"{Root(options)}/#/sifre-sifirla/yeni?userId={userId}&token={Uri.EscapeDataString(token)}";

    public static string ForgotPassword(EmailOptions options) =>
        $"{Root(options)}/#/sifre-sifirla";

    private static string Root(EmailOptions options) => options.AppBaseUrl.TrimEnd('/');
}
