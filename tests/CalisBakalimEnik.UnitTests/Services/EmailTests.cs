using CalisBakalimEnik.Infrastructure.Services;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Services;

public class EmailLinkTests
{
    private static readonly EmailOptions Options = new()
    {
        AppBaseUrl = "https://calisbakalimenik.app/",
    };

    [Fact]
    public void Confirmation_link_escapes_the_token()
    {
        // Identity tokens are base64: '+' in a query string means SPACE, so an
        // unescaped token arrives mangled and confirmation fails with a message
        // that blames the user.
        var link = EmailLinks.ConfirmEmail(
            Options, Guid.Parse("01a0c0ae-bae4-7096-9382-9474ff37dd24"), "ab+cd/ef==");

        link.Should().Be(
            "https://calisbakalimenik.app/#/kayit/dogrula"
            + "?userId=01a0c0ae-bae4-7096-9382-9474ff37dd24&token=ab%2Bcd%2Fef%3D%3D");
    }

    [Fact]
    public void Reset_link_points_at_the_new_password_screen()
    {
        var link = EmailLinks.ResetPassword(Options, Guid.Empty, "t+k");

        link.Should().StartWith("https://calisbakalimenik.app/#/sifre-sifirla/yeni?");
        link.Should().Contain("token=t%2Bk");
    }

    [Fact]
    public void A_trailing_slash_on_the_base_url_does_not_double_up()
    {
        EmailLinks.ForgotPassword(Options)
            .Should().Be("https://calisbakalimenik.app/#/sifre-sifirla");

        EmailLinks.ForgotPassword(new EmailOptions { AppBaseUrl = "https://x.app" })
            .Should().Be("https://x.app/#/sifre-sifirla");
    }
}

public class ResendHtmlTests
{
    [Fact]
    public void Links_survive_escaping_with_their_query_intact()
    {
        var body = "Tıkla:\n\nhttps://calisbakalimenik.app/#/kayit/dogrula?userId=1&token=ab%2Bcd";

        var html = ResendEmailSender.RenderHtml("Doğrula", body);

        // The '&' is encoded to '&amp;' before linking, so the href has to keep
        // it — an anchor that stops at the first '&' loses the token entirely.
        html.Should().Contain(
            "<a href=\"https://calisbakalimenik.app/#/kayit/dogrula?userId=1&amp;token=ab%2Bcd\"");
    }

    [Fact]
    public void Markup_in_the_body_is_escaped_rather_than_rendered()
    {
        // The display name reaches the body, and a display name is user input.
        var html = ResendEmailSender.RenderHtml(
            "Hoş geldin", "Merhaba <script>alert(1)</script> & co");

        html.Should().NotContain("<script>");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void Turkish_characters_are_preserved()
    {
        var html = ResendEmailSender.RenderHtml("Doğrula", "Şifreni değiştir. Çalış!");

        html.Should().Contain("Şifreni değiştir. Çalış!");
        html.Should().Contain("lang=\"tr\"");
    }

    [Fact]
    public void Blank_lines_do_not_become_empty_paragraphs()
    {
        var html = ResendEmailSender.RenderHtml("x", "bir\n\n\niki");

        const string bodyParagraph = "<p style=\"margin:0 0 16px";
        html.Split(bodyParagraph).Length.Should().Be(3, "two body paragraphs, "
            + "not one per blank line");
    }
}
