using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CalisBakalimEnik.Application.Common.Localization;

/// <summary>
/// The API's words in the reader's language (J80).
/// </summary>
/// <remarks>
/// <para>Turkish is the source, and a Turkish sentence is its own key: every
/// other language is a table from the Turkish text to its translation, in
/// <c>Localization/&lt;lang&gt;.json</c>. A sentence with no entry falls back
/// to Turkish rather than to nothing. TextsTests fails when a sentence in the
/// code has no entry in a table, so a missing translation shows up in the
/// build, not on a user's screen.</para>
///
/// <para>Which language: <see cref="Current"/> is the request's, set from
/// Accept-Language by LanguageMiddleware (UI culture only; the formatting
/// culture stays invariant, because a Turkish CurrentCulture changes what
/// ToUpper does to an "i" across the whole code base). Mail and notifications
/// are written outside a request and name the user's saved language with
/// <see cref="In"/>.</para>
/// </remarks>
public static class Texts
{
    public const string Source = "tr";

    /// <summary>The languages the API speaks. The profile accepts these.</summary>
    public static readonly IReadOnlyList<string> Languages = ["tr", "en", "de", "ru"];

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables =
        Languages.Where(l => l != Source).ToDictionary(l => l, Load);

    /// <summary>A supported language for <paramref name="code"/>, else Turkish.</summary>
    public static string Language(string? code)
    {
        var two = (code ?? string.Empty).Trim().ToLowerInvariant();
        if (two.Length > 2) two = two[..2];
        return Languages.Contains(two) ? two : Source;
    }

    /// <summary>The language of the current request.</summary>
    public static string Current => Language(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    /// <summary>
    /// <paramref name="turkish"/> in the current request's language, with
    /// {0}-style arguments formatted invariantly.
    /// </summary>
    public static string T(string turkish, params object?[] args) => In(Current, turkish, args);

    /// <summary><paramref name="turkish"/> in <paramref name="language"/>.</summary>
    public static string In(string? language, string turkish, params object?[] args)
    {
        var lang = Language(language);
        var text = lang != Source && Tables[lang].TryGetValue(turkish, out var translated)
            ? translated
            : turkish;

        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }

    /// <summary>
    /// The translation of a finished Turkish sentence, if the table has it.
    /// For text that was written before the language was known (a problem
    /// document an endpoint built).
    /// </summary>
    public static bool TryTranslate(string? text, string language, out string translated)
    {
        translated = text ?? string.Empty;
        var lang = Language(language);
        if (text is null || lang == Source) return false;
        if (!Tables[lang].TryGetValue(text, out var found)) return false;
        translated = found;
        return true;
    }

    /// <summary>Every Turkish key a table has, for the completeness test.</summary>
    public static IReadOnlyCollection<string> KeysOf(string language) =>
        Tables.TryGetValue(language, out var table) ? table.Keys.ToList() : [];

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        var name = $"CalisBakalimEnik.Application.Common.Localization.{language}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing translation table {name}.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"Empty translation table {name}.");
    }
}
