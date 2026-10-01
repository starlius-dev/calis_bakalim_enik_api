using System.Globalization;
using System.Text.RegularExpressions;
using CalisBakalimEnik.Api.Middleware;
using CalisBakalimEnik.Application.Common.Localization;
using CalisBakalimEnik.Infrastructure.Identity;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Api;

/// <summary>
/// The API in the reader's language (J80).
/// </summary>
public partial class TextsTests
{
    [Fact]
    public void Turkish_is_its_own_key_and_other_languages_are_looked_up()
    {
        Texts.In("tr", "Kod hatalı.").Should().Be("Kod hatalı.");
        Texts.In("en", "Kod hatalı.").Should().Be("Wrong code.");
    }

    [Fact]
    public void A_sentence_with_no_entry_stays_Turkish_rather_than_disappearing()
    {
        Texts.In("en", "Hiçbir tabloda olmayan bir cümle.").Should().Be("Hiçbir tabloda olmayan bir cümle.");
    }

    [Fact]
    public void Arguments_are_formatted_the_same_in_every_culture()
    {
        Texts.In("en", "Aralık en fazla {0} gün olabilir.", 1000)
            .Should().Be("The range can be at most 1000 days.");
    }

    [Theory]
    [InlineData("en-GB", "en")]
    [InlineData("tr-TR", "tr")]
    [InlineData("ja", "tr")]
    [InlineData(null, "tr")]
    public void Unknown_languages_are_Turkish(string? code, string expected)
    {
        Texts.Language(code).Should().Be(expected);
    }

    [Theory]
    [InlineData("en-GB,en;q=0.9", "en")]
    [InlineData("ja;q=1, en;q=0.5", "en")]
    [InlineData("tr;q=0.4, en;q=0.8", "en")]
    [InlineData("ja", "tr")]
    [InlineData("", "tr")]
    public void The_request_language_is_the_best_one_the_API_speaks(string header, string expected)
    {
        LanguageMiddleware.Pick([header]).Should().Be(expected);
    }

    [Fact]
    public void The_request_language_follows_the_UI_culture_only()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            Texts.T("Görev bulunamadı.").Should().Be("Task not found.");
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    /// <summary>
    /// Every Turkish sentence in the code that a reader can see has an English
    /// entry. Without this, a new message ships in Turkish to English readers
    /// and nothing anywhere says so.
    /// </summary>
    [Fact]
    public void Every_Turkish_sentence_in_the_code_has_an_English_entry()
    {
        var english = Texts.KeysOf("en").ToHashSet();
        var missing = TurkishSentences().Where(s => !english.Contains(s)).ToList();

        missing.Should().BeEmpty(
            "add these to src/CalisBakalimEnik.Application/Common/Localization/en.json");
    }

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    public void Every_mail_exists_in_every_language_without_dashes(string language)
    {
        var mails = new[]
        {
            AccountEmails.Requested(language, "Zeynep", "01.10.2026 09:00"),
            AccountEmails.Cancelled(language, "Zeynep"),
            AccountEmails.Erased(language, "Zeynep"),
            AuthEmails.RegisterAttempt(language, "https://x/#/sifre-sifirla"),
            AuthEmails.ConfirmEmail(language, "Zeynep", "https://x/#/kayit/dogrula"),
            AuthEmails.ResetPassword(language, "https://x/#/sifre-sifirla/yeni"),
            AuthEmails.SignInCode(language, "123456", 10),
        };

        mails.Should().AllSatisfy(m =>
        {
            m.Subject.Should().NotBeNullOrWhiteSpace();
            m.Body.Should().NotContainAny("—", "–");
        });

        if (language == "en")
            mails.Should().AllSatisfy(m => m.Body.Should().NotContain("Merhaba"));
    }

    // ── the scan, the same rules as tools/turkish_strings.py ─────────────

    private static readonly HashSet<string> SkipFiles =
    [
        "ExerciseCatalogue.cs", "QuietHours.cs", "EmailOptions.cs", "Program.cs",
        "ResendEmailSender.cs", "PartitionMaintenance.cs",
        // Mail templates carry their own version per language.
        "AccountEmails.cs", "AuthEmails.cs", "PersonalDetailEmails.cs",
    ];

    [GeneratedRegex(@"(?<![@$""])""((?:[^""\\\n]|\\.)*)""")]
    private static partial Regex Literal();

    [GeneratedRegex("[çğıöşüÇĞİÖŞÜ]")]
    private static partial Regex Turkish();

    private static IEnumerable<string> TurkishSentences()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var seen = new HashSet<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var norm = file.Replace('\\', '/');
            if (norm.Contains("/obj/") || norm.Contains("/bin/") || norm.Contains("Migrations"))
                continue;
            if (SkipFiles.Contains(Path.GetFileName(file))) continue;

            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("//") || trimmed.StartsWith('*')) continue;

                foreach (Match m in Literal().Matches(line))
                {
                    var value = m.Groups[1].Value.Replace("\\\"", "\"");
                    if (Turkish().IsMatch(value) && value != "Çalış Bakalım Enik" && seen.Add(value))
                        yield return value;
                }
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
